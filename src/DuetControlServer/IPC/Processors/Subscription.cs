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
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                _jsonModel = JObject.FromObject(Model.Provider.Get, JsonHelper.DefaultSerializer);
                _messages.AddRange(Model.Provider.Get.Messages);
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
                    lock (_messages)
                    {
                        _jsonModel["messages"] = JArray.FromObject(_messages, JsonHelper.DefaultSerializer);
                        _messages.Clear();
                    }
                    json = _jsonModel.ToString(Formatting.None);
                    _jsonModel.Remove("messages");
                }
                await Connection.Send(json + "\n");
                
                do
                {
                    // Wait for acknowledgement
                    if (!patchWasEmpty)
                    {
                        BaseCommand command = await Connection.ReceiveCommand();
                        if (command == null)
                        {
                            return;
                        }

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
                            lock (_messages)
                            {
                                _jsonModel["messages"] = JArray.FromObject(_messages, JsonHelper.DefaultSerializer);
                                _messages.Clear();
                            };
                            json = _jsonModel.ToString(Formatting.None);
                            _jsonModel.Remove("messages");
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
                            _lastModel = _jsonModel;
                        }

                        // Compact layers
                        if (patch.ContainsKey("job") && patch.Value<JObject>("job").ContainsKey("layers"))
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
                            patch["messages"] = JArray.FromObject(_messages, JsonHelper.DefaultSerializer);
                            _messages.Clear();
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
            finally
            {
                _subscriptions.TryRemove(this, out _);
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

        /// <summary>
        /// Called to notify the subscribers about a model update
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task ModelUpdated()
        {
            // This is probably really slow and needs to be improved!
            JObject newModel;
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                newModel = JObject.FromObject(Model.Provider.Get, JsonHelper.DefaultSerializer);
                newModel.Remove("messages");
            }

            // Notify subscribers
            foreach (var pair in _subscriptions)
            {
                pair.Key.Update(newModel);
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
