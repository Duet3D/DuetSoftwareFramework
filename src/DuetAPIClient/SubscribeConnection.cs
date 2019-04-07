using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using DuetAPI.Machine;
using DuetAPI.Utility;
using DuetAPIClient.Exceptions;
using Newtonsoft.Json.Linq;

namespace DuetAPIClient
{
    /// <summary>
    /// Connection class for subscribing to model updates
    /// </summary>
    /// <seealso cref="ConnectionMode.Subscribe"/>
    public class SubscribeConnection : BaseConnection
    {
        /// <summary>
        /// Creates a new connection in intercept mode
        /// </summary>
        public SubscribeConnection() : base(ConnectionMode.Command) { }

        /// <summary>
        /// Establishes a connection to the given UNIX socket file
        /// </summary>
        /// <param name="mode">Subscription mode</param>
        /// <param name="socketPath">Path to the UNIX socket file</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="IncompatibleVersionException">API level is incompatible</exception>
        /// <exception cref="IOException">Connection mode is unavailable</exception>
        public Task Connect(SubscriptionMode mode, string socketPath = "/tmp/duet.sock", CancellationToken cancellationToken = default(CancellationToken))
        {
            SubscribeInitMessage initMessage = new SubscribeInitMessage { SubscriptionMode = mode };
            return base.Connect(initMessage, socketPath, cancellationToken);
        }
        
        /// <summary>
        /// Retrieves the full object model of the machine
        /// In subscription mode this is the first command that has to be called once a connection has been established.
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>The current machine model</returns>
        public async Task<MachineModel> GetMachineModel(CancellationToken cancellationToken = default(CancellationToken))
        {
            MachineModel model = await Receive<MachineModel>(cancellationToken);
            await Send(new Acknowledge());
            return model;
        }
        
        /// <summary>
        /// Optimized method to query the machine model JSON in any mode.
        /// May be used to get machine model patches as well.
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Machine model JSON</returns>
        public async Task<string> GetSerializedMachineModel(CancellationToken cancellationToken = default(CancellationToken))
        {
            string json = await ReceiveSerializedJson(cancellationToken);
            await Send(new Acknowledge());
            return json;
        }
        
        /// <summary>
        /// Receive a (partial) machine model update.
        /// If the subscription mode is set to <see cref="SubscriptionMode.Patch"/>, new update patches of the object model
        /// need to be applied manually. This method is intended to receive such fragments.
        /// </summary>
        /// <param name="cancellationToken">An optional cancellation token</param>
        /// <returns>The partial update JSON</returns>
        /// <seealso cref="GetMachineModel"/>
        /// <seealso cref="JsonHelper.PatchObject(object, JObject)"/>
        public async Task<JObject> GetMachineModelPatch(CancellationToken cancellationToken = default(CancellationToken))
        {
            JObject patch = await ReceiveJson(cancellationToken);
            await Send(new Acknowledge());
            return patch;
        }
    }
}