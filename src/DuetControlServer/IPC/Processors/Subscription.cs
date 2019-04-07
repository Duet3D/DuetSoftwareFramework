using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using DuetAPI.Utility;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nito.AsyncEx;

namespace DuetControlServer.IPC.Processors
{
    /// <summary>
    /// Subscription processor that notifies clients about object model changes.
    /// There is no point in deserializing the object model here so only the JSON representation is kept here.
    /// </summary>
    public class Subscription : Base
    {
        /// <summary>
        /// List of supported commands in this mode
        /// </summary>
        public static readonly Type[] SupportedCommands =
        {
            typeof(Acknowledge)
        };
        
        private static readonly ConcurrentDictionary<Subscription, SubscriptionMode> _subscriptions = new ConcurrentDictionary<Subscription, SubscriptionMode>();

        private readonly SubscriptionMode _mode;

        private JObject _jsonModel, _lastModel;
        private List<Message> _messages = new List<Message>();
        private AsyncAutoResetEvent _updateAvailableEvent = new AsyncAutoResetEvent();
        
        /// <summary>
        /// Constructor of the subscription processor
        /// </summary>
        /// <param name="conn">Connection instance</param>
        /// <param name="initMessage">Initialization message</param>
        public Subscription(Connection conn, ClientInitMessage initMessage) : base(conn, initMessage)
        {
            _mode = (initMessage as SubscribeInitMessage).SubscriptionMode;
        }
        
        /// <summary>
        /// Task that keeps pushing model updates to the client
        /// </summary>
        /// <returns>Task that represents the lifecycle of a connection</returns>
        public override async Task Process()
        {
            // Initialize the machine model and register this subscriber
            using (await Model.Provider.AccessReadOnly())
            {
                _jsonModel = JObject.FromObject(Model.Provider.Get, JsonHelper.DefaultSerializer);
            }
            _lastModel = _jsonModel;
            _subscriptions.TryAdd(this, _mode);

            try
            {
                bool patchWasEmpty = false;

                // Send over the full machine model once
                string json;
                lock (_jsonModel)
                {
                    JArray originalMessages = ReadMessages();
                    json = _jsonModel.ToString(Formatting.None);
                    _jsonModel["messages"] = originalMessages;
                }
                await Connection.Send(json + "\n");
                
                do
                {
                    // Wait for acknowledgement
                    if (!patchWasEmpty)
                    {
                        var command = await Connection.ReceiveCommand();
                        if (!SupportedCommands.Contains(command.GetType()))
                        {
                            throw new ArgumentException($"Invalid command {command.Command} (wrong mode?)");
                        }
                    }

                    // Wait for another update
                    await _updateAvailableEvent.WaitAsync(Program.CancelSource.Token);
                    
                    // Send over the next update
                    if (_mode == SubscriptionMode.Full)
                    {
                        // Send the entire object model in Full mode
                        lock (_jsonModel)
                        {
                            JArray originalMessages = ReadMessages();
                            json = _jsonModel.ToString(Formatting.None);
                            _jsonModel["messages"] = originalMessages;
                        }
                        await Connection.Send(json + "\n");
                    }
                    else
                    {
                        // Only create a patch in Patch mode
                        JObject patch;
                        lock (_jsonModel)
                        {
                            patch = JsonHelper.DiffObject(_lastModel, _jsonModel);
                        }

                        // Compact layers
                        if (patch.ContainsKey("job") && patch.ContainsKey("layers"))
                        {
                            JArray layersArray = patch["job"].Value<JArray>("layers");
                            while (!layersArray[0].HasValues)
                            {
                                layersArray.RemoveAt(0);
                            }
                        }

                        // Write pending messages
                        lock (_messages)
                        {
                            if (_messages.Count != 0)
                            {
                                JArray messageArray = patch.ContainsKey("messages") ? patch.Value<JArray>("messages") : new JArray();
                                foreach (Message message in _messages)
                                {
                                    messageArray.Add(JObject.FromObject(message, JsonHelper.DefaultSerializer));
                                }
                                patch["messages"] = messageArray;
                            }
                        }
                        
                        // Send it over unless it is empty
                        if (patch.HasValues)
                        {
                            json = patch.ToString(Formatting.None);
                            await Connection.Send(json + "\n");
                        }
                        else
                        {
                            patchWasEmpty = true;
                        }
                    }
                } while (!Program.CancelSource.IsCancellationRequested);
            }
            catch (Exception e)
            {
                if (Connection.IsConnected)
                {
                    // Inform the client about this error
                    await Connection.SendResponse(e);
                }
                else
                {
                    _subscriptions.TryRemove(this, out SubscriptionMode dummy);
                    throw;
                }
            } 
        }

        /// <summary>
        /// Enqueue a generic message for output
        /// </summary>
        /// <param name="message">New message</param>
        /// <returns>Whether the message could be stored</returns>
        public static bool Output(Message message)
        {
            if (_subscriptions.Count == 0)
            {
                return false;
            }

            foreach (var pair in _subscriptions)
            {
                pair.Key.AddMessage(message);
            }
            return true;
        }

        private void AddMessage(Message message)
        {
            lock (_messages)
            {
                _messages.Add(message);
            }
            _updateAvailableEvent.Set();
        }

        private JArray ReadMessages()
        {
            JArray messages = _jsonModel.Value<JArray>("messages");
            lock (_messages)
            {
                if (_messages.Count != 0)
                {
                    JArray clone = (JArray)messages.DeepClone();

                    foreach (Message message in _messages)
                    {
                        messages.Add(JObject.FromObject(message, JsonHelper.DefaultSerializer));
                    }

                    messages = clone;
                }
                _messages.Clear();
            }
            return messages;
        }

        /// <summary>
        /// Called to notify the subscribers about a model update
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task ModelUpdated()
        {
            // This is probably really slow and needs to be improved!
            JObject newModel;
            using (await Model.Provider.AccessReadOnly())
            {
                newModel = JObject.FromObject(Model.Provider.Get, JsonHelper.DefaultSerializer);
            }

            if (_subscriptions.Count != 0)
            {
                // Notify subscribers
                foreach (var pair in _subscriptions)
                {
                    pair.Key.Update(newModel);
                }

                // Clear messages once they have been sent out at least once
                using (await Model.Provider.AccessReadWrite())
                {
                    Model.Provider.Get.Messages.Clear();
                }
            }
        }

        private void Update(JObject newModel)
        {
            lock (_jsonModel)
            {
                _jsonModel = newModel;
            }
            _updateAvailableEvent.Set();
        }
    }
}
