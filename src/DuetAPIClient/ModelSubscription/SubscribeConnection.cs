using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;

namespace DuetAPIClient
{
    /// <summary>
    /// Connection class for subscribing to model updates
    /// </summary>
    /// <seealso cref="ConnectionMode.Subscribe"/>
    public sealed class SubscribeConnection : BaseConnection
    {
        /// <summary>
        /// Creates a new connection in subscriber mode
        /// </summary>
        public SubscribeConnection() : base(ConnectionMode.Subscribe) { }

        /// <summary>
        /// Mode of the subscription
        /// </summary>
        public SubscriptionMode Mode { get; private set; }

        /// <summary>
        /// Delimited filter expression
        /// </summary>
        /// <seealso cref="Filters"/>
        [Obsolete]
        public string Filter { get; private set; }

        /// <summary>
        /// Filter expressions
        /// </summary>
        /// <seealso cref="SubscribeInitMessage.Filter"/>
        public List<string> Filters { get; } = new List<string>();

        /// <summary>
        /// Establishes a connection to the given UNIX socket file
        /// </summary>
        /// <param name="mode">Subscription mode</param>
        /// <param name="filter">Optional delimited filter string</param>
        /// <param name="socketPath">Path to the UNIX socket file</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="IncompatibleVersionException">API level is incompatible</exception>
        /// <exception cref="IOException">Connection mode is unavailable</exception>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Init message could not be processed</exception>
        [Obsolete]
        public Task Connect(SubscriptionMode mode, string filter = null, string socketPath = Defaults.FullSocketPath, CancellationToken cancellationToken = default)
        {
            Mode = mode;
            Filter = filter;
            Filters.Clear();

            SubscribeInitMessage initMessage = new SubscribeInitMessage { SubscriptionMode = mode, Filter = Filter };
            return Connect(initMessage, socketPath, cancellationToken);
        }

        /// <summary>
        /// Establishes a connection to the given UNIX socket file
        /// </summary>
        /// <param name="mode">Subscription mode</param>
        /// <param name="filters">Optional filter strings</param>
        /// <param name="socketPath">Path to the UNIX socket file</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="IncompatibleVersionException">API level is incompatible</exception>
        /// <exception cref="IOException">Connection mode is unavailable</exception>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Init message could not be processed</exception>
        public Task Connect(SubscriptionMode mode, IEnumerable<string> filters = null, string socketPath = Defaults.FullSocketPath, CancellationToken cancellationToken = default)
        {
            Mode = mode;
            Filters.Clear();
            if (filters != null)
            {
                Filters.AddRange(filters);
            }

            SubscribeInitMessage initMessage = new SubscribeInitMessage { SubscriptionMode = mode, Filters = Filters };
            return Connect(initMessage, socketPath, cancellationToken);
        }
        
        /// <summary>
        /// Retrieves the full object model of the machine
        /// In subscription mode this is the first command that has to be called once a connection has been established.
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>The current machine model</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Receipt could not be acknowledged</exception>
        /// <seealso cref="SbcPermissions.ObjectModelRead"/>
        /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
        public async Task<ObjectModel> GetMachineModel(CancellationToken cancellationToken = default)
        {
            ObjectModel model = await Receive<ObjectModel>(cancellationToken);
            await Send(new Acknowledge(), cancellationToken);
            return model;
        }

        /// <summary>
        /// Optimized method to query the machine model UTF-8 JSON in any mode.
        /// May be used to get machine model patches as well.
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Machine model JSON</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Receipt could not be acknowledged</exception>
        /// <seealso cref="SbcPermissions.ObjectModelRead"/>
        /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
        public async Task<MemoryStream> GetSerializedMachineModel(CancellationToken cancellationToken = default)
        {
            MemoryStream json = await JsonHelper.ReceiveUtf8Json(_unixSocket, cancellationToken);
            await Send(new Acknowledge(), cancellationToken);
            return json;
        }
        
        /// <summary>
        /// Receive a (partial) machine model update.
        /// If the subscription mode is set to <see cref="SubscriptionMode.Patch"/>, new update patches of the object model
        /// need to be applied manually. This method is intended to receive such fragments.
        /// </summary>
        /// <param name="cancellationToken">An optional cancellation token</param>
        /// <returns>The partial update JSON</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Receipt could not be acknowledged</exception>
        /// <seealso cref="GetMachineModel"/>
        /// <seealso cref="SbcPermissions.ObjectModelRead"/>
        /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
        public async Task<JsonDocument> GetMachineModelPatch(CancellationToken cancellationToken = default)
        {
            JsonDocument patch = await ReceiveJson(cancellationToken);
            await Send(new Acknowledge(), cancellationToken);
            return patch;
        }
    }
}