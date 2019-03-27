using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Machine;
using DuetControlServer.SPI;
using Newtonsoft.Json;
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
        private JObject _jsonModel, _lastModel;
        
        private readonly AutoResetEvent _updateEvent = new AutoResetEvent(false);
        
        /// <summary>
        /// Constructor of the subscription processor
        /// </summary>
        /// <param name="conn">Connection instance</param>
        /// <param name="initMessage">Initialization message</param>
        public Subscription(Connection conn, ClientInitMessage initMessage) : base(conn, initMessage)
        {
            _mode = (initMessage as SubscribeInitMessage).SubscriptionMode;
            
            _jsonModel = JObject.FromObject(ModelProvider.Current, JsonHelper.DefaultSerializer);
            _lastModel = _jsonModel;
            
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
                // Send over the full machine model initially
                string json;
                lock (_jsonModel)
                {
                    json = _jsonModel.ToString(Formatting.None);
                }
                await Connection.Send(json + "\n");
                
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
                    
                    // Send over the next update
                    if (_mode == SubscriptionMode.Full)
                    {
                        // Send the entire object model in Full mode
                        lock (_jsonModel)
                        {
                            json = _jsonModel.ToString(Formatting.None);
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
                        
                        // Send it over
                        json = patch.ToString(Formatting.None);
                        await Connection.Send(json + "\n");
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
        public static void Update(Model objectModel)
        {
            JObject newModel = JObject.FromObject(objectModel, JsonHelper.DefaultSerializer);
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
