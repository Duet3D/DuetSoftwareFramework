using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
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
        /// Code channel to receive messages from. If not set, only generic messages are forwarded (as in v3.3 and earlier)
        /// </summary>
        /// <remarks>
        /// This has no effect in <see cref="SubscriptionMode.Full"/> mode
        /// </remarks>
        public CodeChannel? Channel { get; private set; }

        /// <summary>
        /// Mode of the subscription
        /// </summary>
        public SubscriptionMode Mode { get; private set; }

        /// <summary>
        /// Delimited filter expression
        /// </summary>
        /// <seealso cref="Filters"/>
        [Obsolete("Use Filters instead")]
        public string? Filter { get; private set; }

        /// <summary>
        /// Filter expressions
        /// </summary>
        /// <seealso cref="SubscribeInitMessage.Filter"/>
        public List<string> Filters { get; } = [];

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
        [Obsolete("Use the new Connect overload with a filter list instead")]
        public Task Connect(SubscriptionMode mode, string filter, string socketPath = Defaults.FullSocketPath, CancellationToken cancellationToken = default)
        {
            Mode = mode;
            Filter = filter;
            Filters.Clear();

            SubscribeInitMessage initMessage = new() { SubscriptionMode = mode, Filter = Filter };
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
        public Task Connect(SubscriptionMode mode, IEnumerable<string>? filters = null, string socketPath = Defaults.FullSocketPath, CancellationToken cancellationToken = default)
        {
            Mode = mode;
            Filters.Clear();
            if (filters is not null)
            {
                Filters.AddRange(filters);
            }

            SubscribeInitMessage initMessage = new() { SubscriptionMode = mode, Filters = Filters };
            return Connect(initMessage, socketPath, cancellationToken);
        }

        /// <summary>
        /// Establishes a connection to the given UNIX socket file
        /// </summary>
        /// <param name="mode">Subscription mode</param>
        /// <param name="channel">Optional code channel to receive messages from (not applicable in Full mode)</param>
        /// <param name="filters">Optional filter strings</param>
        /// <param name="socketPath">Path to the UNIX socket file</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="IncompatibleVersionException">API level is incompatible</exception>
        /// <exception cref="IOException">Connection mode is unavailable</exception>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Init message could not be processed</exception>
        public Task Connect(SubscriptionMode mode, CodeChannel? channel, IEnumerable<string>? filters = null, string socketPath = Defaults.FullSocketPath, CancellationToken cancellationToken = default)
        {
            Mode = mode;
            Channel = channel;
            Filters.Clear();
            if (filters is not null)
            {
                Filters.AddRange(filters);
            }

            SubscribeInitMessage initMessage = new() { SubscriptionMode = mode, Channel = Channel, Filters = Filters };
            return Connect(initMessage, socketPath, cancellationToken);
        }

        /// <summary>
        /// Retrieves the full machine model of the machine
        /// In subscription mode this is the first command that has to be called once a connection has been established.
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>The current machine model</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Receipt could not be acknowledged</exception>
        /// <seealso cref="SbcPermissions.ObjectModelRead"/>
        /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
        [Obsolete("Use GetObjectModel instead")]
        public Task<ObjectModel> GetMachineModel(CancellationToken cancellationToken = default) => GetObjectModel(cancellationToken);

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
        public async Task<ObjectModel> GetObjectModel(CancellationToken cancellationToken = default)
        {
            if (Mode == SubscriptionMode.Full)
            {
                await SendCommand(new Acknowledge(), cancellationToken);
            }
            using MemoryStream jsonStream = await JsonHelper.ReceiveUtf8Json(_unixSocket, cancellationToken);

            ObjectModel Deserialize() {
                Utf8JsonReader reader = new(jsonStream.ToArray());

                ObjectModel model = new();
                model.UpdateFromJsonReader(ref reader, false);
                return model;
            }
            return Deserialize();
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
        [Obsolete("Use GetSerializedObjectModel instead")]
        public async Task<MemoryStream> GetSerializedMachineModel(CancellationToken cancellationToken = default)
        {
            await SendCommand(new Acknowledge(), cancellationToken);
            return await JsonHelper.ReceiveUtf8Json(_unixSocket, cancellationToken);
        }

        /// <summary>
        /// Optimized method to query the object model UTF-8 JSON in any mode.
        /// May be used to get machine model patches as well.
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Machine model JSON</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Receipt could not be acknowledged</exception>
        /// <seealso cref="SbcPermissions.ObjectModelRead"/>
        /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>g
        public async Task<MemoryStream> GetSerializedObjectModel(CancellationToken cancellationToken = default)
        {
            await SendCommand(new Acknowledge(), cancellationToken);
            return await JsonHelper.ReceiveUtf8Json(_unixSocket, cancellationToken);
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
        /// <seealso cref="GetObjectModel"/>
        /// <seealso cref="SbcPermissions.ObjectModelRead"/>
        /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
        [Obsolete("Use GetObjectModelPatch instead")]
        public Task<JsonDocument> GetMachineModelPatch(CancellationToken cancellationToken = default) => GetObjectModelPatch(cancellationToken);

        /// <summary>
        /// Receive a (partial) object model update.
        /// If the subscription mode is set to <see cref="SubscriptionMode.Patch"/>, new update patches of the object model
        /// need to be applied manually. This method is intended to receive such fragments.
        /// </summary>
        /// <param name="cancellationToken">An optional cancellation token</param>
        /// <returns>The partial update JSON</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Receipt could not be acknowledged</exception>
        /// <seealso cref="GetObjectModel"/>
        /// <seealso cref="SbcPermissions.ObjectModelRead"/>
        /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
        public async Task<JsonDocument> GetObjectModelPatch(CancellationToken cancellationToken = default)
        {
            await SendCommand(new Acknowledge(), cancellationToken);
            return await ReceiveJsonDocument(cancellationToken);
        }
    }
}