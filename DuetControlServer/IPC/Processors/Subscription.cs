using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.Connection;
using Newtonsoft.Json.Linq;

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

        private readonly AutoResetEvent _updateEvent = new AutoResetEvent(false);
        private JObject _jsonModel, _lastModel;
        
        /// <summary>
        /// Constructor of the subscription processor
        /// </summary>
        /// <param name="conn">Connection instance</param>
        /// <param name="initMessage">Initialization message</param>
        public Subscription(Connection conn, ClientInitMessage initMessage) : base(conn, initMessage)
        {
            _jsonModel = JObject.FromObject(SPI.ModelProvider.Current, DuetAPI.JsonHelper.DefaultSerializer);
            _mode = (initMessage as SubscribeInitMessage).SubscriptionMode;
            
            _subscriptions.TryAdd(this, _mode);
        }
        
        /// <summary>
        /// Task that keeps pushing model updates to the client
        /// </summary>
        /// <returns>Task that represents the lifecycle of a connection</returns>
        public override async Task Process()
        {
            try
            {
                // Send over the full object model initially
                JObject clone;
                lock (_jsonModel)
                {
                    clone = (JObject)_jsonModel.DeepClone();
                }
                await Connection.Send(clone);
                
                do
                {
                    // Wait for acknowledgement
                    BaseCommand command = await Connection.ReceiveCommand();
                    if (!SupportedCommands.Contains(command.GetType()))
                    {
                        throw new ArgumentException($"Invalid command {command.Command} (wrong mode?)");
                    }
                    
                    // Wait for another update
                    await WaitForUpdate();
                    
                    // Make a clone of the object model to avoid race conditions
                    lock (_jsonModel)
                    {
                        clone = (JObject)_jsonModel.DeepClone();
                    }
                    
                    // Send over the update
                    if (_mode == SubscriptionMode.Full)
                    {
                        // Send the full object model in Full mode
                        await Connection.Send(clone);
                    }
                    else
                    {
                        // Only send the patch in Patch mode
                        JObject patch = DuetAPI.JsonHelper.DiffObject(_lastModel, clone);
                        _lastModel = clone;
                        
                        await Connection.Send(patch);
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

        private async Task WaitForUpdate()
        {
            await Task.Run(() => _updateEvent.WaitOne(), Program.CancelSource.Token);
        }

        private void Notify(JObject newModel)
        {
            lock (_jsonModel)
            {
                _jsonModel = newModel;
            }
            _updateEvent.Set();
        }

        /// <summary>
        /// Called to notify the subscribers about a model update
        /// </summary>
        /// <param name="objectModel">Updated full object model</param>
        public static void Update(DuetAPI.Machine.Model objectModel)
        {
            JObject newModel = JObject.FromObject(objectModel, DuetAPI.JsonHelper.DefaultSerializer);
            if (_subscriptions.Count != 0)
            {
                foreach (var pair in _subscriptions)
                {
                    pair.Key.Notify(newModel);
                }
            }
        }
    }
}
