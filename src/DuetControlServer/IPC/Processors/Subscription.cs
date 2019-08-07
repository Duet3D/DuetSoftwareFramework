using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using DuetAPI.Machine;
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
        
        private static readonly List<Subscription> _subscriptions = new List<Subscription>();

        private readonly SubscriptionMode _mode;
        private readonly MachineModel _model;
        private readonly List<Message> _messages = new List<Message>();
        private readonly AsyncAutoResetEvent _updateAvailableEvent = new AsyncAutoResetEvent();
        
        /// <summary>
        /// Constructor of the subscription processor
        /// </summary>
        /// <param name="conn">Connection instance</param>
        /// <param name="initMessage">Initialization message</param>
        public Subscription(Connection conn, ClientInitMessage initMessage) : base(conn, initMessage)
        {
            lock (_subscriptions)
            {
                _subscriptions.Add(this);
            }

            _mode = (initMessage as SubscribeInitMessage).SubscriptionMode;
            using (Model.Provider.AccessReadOnly())
            {
                _model = (MachineModel)Model.Provider.Get.Clone();
            }
        }
        
        /// <summary>
        /// Task that keeps pushing model updates to the client
        /// </summary>
        /// <returns>Task that represents the lifecycle of a connection</returns>
        public override async Task Process()
        {
            try
            {
                // First send over the full machine model
                JObject currentObject, lastObject, patch = null;
                lock (_model)
                {
                    currentObject = lastObject = JObject.FromObject(_model, JsonHelper.DefaultSerializer);
                    _model.Messages.Clear();
                }
                await Connection.Send(currentObject.ToString(Formatting.None) + "\n");

                do
                {
                    // Wait for an acknowledgement from the client if anything was sent before
                    if (patch == null || patch.HasValues)
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
                    if (_mode == SubscriptionMode.Patch)
                    {
                        lastObject = currentObject;
                    }
                    await _updateAvailableEvent.WaitAsync(Program.CancelSource.Token);

                    // Get the updated object model
                    lock (_model)
                    {
                        using (Model.Provider.AccessReadOnly())
                        {
                            // NB: This could be further improved so that all the JSON tokens are written via the INotifyPropertyChanged events
                            _model.Assign(Model.Provider.Get);
                        }
                        lock (_messages)
                        {
                            ListHelpers.AssignList(_model.Messages, _messages);
                            _messages.Clear();
                        }
                        currentObject = JObject.FromObject(_model, JsonHelper.DefaultSerializer);
                    }

                    // Provide the model update
                    if (_mode == SubscriptionMode.Full)
                    {
                        // Send the entire object model in Full mode
                        await Connection.Send(currentObject.ToString(Formatting.None) + "\n");
                    }
                    else
                    {
                        // Only create a diff in Patch mode
                        patch = JsonHelper.DiffObject(lastObject, currentObject);

                        // Compact the job layers. There is no point in sending them all every time an update occurs
                        if (patch.ContainsKey("job") && patch.Value<JObject>("job").ContainsKey("layers"))
                        {
                            JArray layersArray = patch["job"].Value<JArray>("layers");
                            while (layersArray.Count > 0 && !layersArray[0].HasValues)
                            {
                                layersArray.RemoveAt(0);
                            }
                        }

                        // Send the patch unless it is empty
                        if (patch.HasValues)
                        {
                            await Connection.Send(patch.ToString(Formatting.None) + "\n");
                        }
                    }
                } while (!Program.CancelSource.IsCancellationRequested);
            }
            finally
            {
                lock (_subscriptions)
                {
                    _subscriptions.Remove(this);
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

            lock (_subscriptions)
            {
                foreach (Subscription subscription in _subscriptions)
                {
                    subscription.AddMessage(message);
                }
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
        public static void ModelUpdated()
        {
            lock (_subscriptions)
            {
                foreach (Subscription subscription in _subscriptions)
                {
                    // Inform every subscriber about new data
                    subscription._updateAvailableEvent.Set();
                }
            }
        }
    }
}
