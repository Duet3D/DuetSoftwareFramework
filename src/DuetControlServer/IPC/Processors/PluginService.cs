using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection.InitMessages;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Commands;
using DuetSharedLibrary;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.IPC.Processors;

/// <summary>
/// IPC processor for plugin services
/// </summary>
public sealed class PluginService : IProcessor, IDisposable
{
    /// <summary>
    /// Monitor for the service interfaces
    /// </summary>
    private static readonly AsyncMonitor _monitor = new();

    /// <summary>
    /// Monitor for the root service interfaces
    /// </summary>
    private static readonly AsyncMonitor _rootMonitor = new();

    /// <summary>
    /// Indicates if a service is currently connected
    /// </summary>
    private static bool _serviceConnected;

    /// <summary>
    /// Indicates if a service is currently connected
    /// </summary>
    private static bool _rootServiceConnected;

    /// <summary>
    /// Processor of the currently registered plugin service
    /// </summary>
    private static PluginService? _service;

    /// <summary>
    /// Processor of the currently registered root plugin service
    /// </summary>
    private static PluginService? _rootService;

    /// <summary>
    /// Queue of pending service commands. The TaskCompletionSource receives the deserialized result (null for
    /// untyped/void commands)
    /// </summary>
    private static readonly Queue<Tuple<BaseCommand, TaskCompletionSource<object?>>> _pendingCommands = new();

    /// <summary>
    /// Queue of pending service commands for the root service, same layout as <see cref="_pendingCommands"/>
    /// </summary>
    private static readonly Queue<Tuple<BaseCommand, TaskCompletionSource<object?>>> _pendingRootCommands = new();

    /// <summary>
    /// Check if the requested plugin service is currently connected
    /// </summary>
    /// <param name="asRoot">Whether to check the root plugin service</param>
    /// <returns>True if the service is connected</returns>
    public static bool IsConnected(bool asRoot) => asRoot ? _rootServiceConnected : _serviceConnected;

    /// <summary>
    /// Peer PID of the currently-connected non-root plugin service, or 0 if none is connected. Used by DCS to
    /// authenticate DPS's internal command connections so that a plugin re-execing DuetPluginService (with LD_PRELOAD
    /// or otherwise) cannot masquerade as the real DPS - its PID won't match
    /// </summary>
    public static int ServicePid { get; private set; }

    /// <summary>
    /// Perform a command via the plugin service
    /// </summary>
    /// <param name="command">Command to perform</param>
    /// <param name="asRoot">Send it to the service running as root</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public static Task PerformCommandAsync(BaseCommand command, bool asRoot, CancellationToken cancellationToken = default)
    {
        return EnqueueCommandAsync(command, asRoot, cancellationToken);
    }

    /// <summary>
    /// Perform a command via the plugin service and return its typed result
    /// </summary>
    /// <typeparam name="T">Expected result type (must be registered in <see cref="CommandContext"/>)</typeparam>
    /// <param name="command">Command to perform</param>
    /// <param name="asRoot">Send it to the service running as root</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Deserialized command result</returns>
    public static async Task<T?> PerformCommandAsync<T>(BaseCommand command, bool asRoot, CancellationToken cancellationToken = default)
    {
        return (T?)await EnqueueCommandAsync(command, asRoot, cancellationToken);
    }

    private static async Task<object?> EnqueueCommandAsync(BaseCommand command, bool asRoot, CancellationToken cancellationToken)
    {
        TaskCompletionSource<object?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using (await (asRoot ? _rootMonitor : _monitor).EnterAsync(cancellationToken))
        {
            if (asRoot)
            {
                if (!_rootServiceConnected)
                {
                    throw new InvalidOperationException("Cannot perform command because the plugin service (root) is not started");
                }
                _pendingRootCommands.Enqueue(new Tuple<BaseCommand, TaskCompletionSource<object?>>(command, tcs));
                _rootMonitor.Pulse();
            }
            else
            {
                if (!_serviceConnected)
                {
                    throw new InvalidOperationException("Cannot perform command because the plugin service is not started");
                }
                _pendingCommands.Enqueue(new Tuple<BaseCommand, TaskCompletionSource<object?>>(command, tcs));
                _monitor.Pulse();
            }
        }
        return await tcs.Task;
    }

    /// <summary>
    /// Connection to the IPC client served by this processor
    /// </summary>
    public Connection Connection { get; }

    // Private fields
    private readonly CommandFactory _commandFactory;
    private readonly Model.ObjectModel _model;
    private readonly Settings _settings;

    /// <summary>
    /// Cancellation source to terminate this processor when another plugin service takes over
    /// </summary>
    private readonly CancellationTokenSource _terminationCts = new();

    /// <summary>
    /// Constructor of the plugin runner proxy processor
    /// </summary>
    /// <param name="conn">Connection instance</param>
    /// <param name="initMessage">Initialization message from the client</param>
    /// <param name="commandFactory">Command factory to create commands</param>
    /// <param name="model">Object model instance</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settings">Settings</param>
    public PluginService(Connection conn,
        ClientInitMessage initMessage,
        CommandFactory commandFactory,
        Model.ObjectModel model,
        ILogger<PluginService> logger,
        IOptions<Settings> settings)
    {
        Connection = conn;
        _commandFactory = commandFactory;
        _model = model;
        _settings = settings.Value;

        logger.LogDebug("PluginService processor added for IPC#{Id}", conn.Id);
    }

    /// <summary>
    /// Dispose this instance
    /// </summary>
    public void Dispose() => _terminationCts.Dispose();

    /// <summary>
    /// Handles the remote connection
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the worker</param>
    /// <returns>Asynchronous task</returns>
    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        if (!_settings.PluginSupport)
        {
            throw new NotSupportedException("Plugin support has been disabled");
        }

        // Terminate this processor as well when another plugin service takes over this registration
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _terminationCts.Token);
        cancellationToken = cts.Token;

        // Try to register this plugin service
        AsyncMonitor monitor = Connection.IsRoot ? _rootMonitor : _monitor;
        using (await monitor.EnterAsync(cancellationToken))
        {
            if (Connection.IsRoot)
            {
                if (_rootServiceConnected && !TerminateIfGone(_rootService))
                {
                    throw new InvalidOperationException("Plugin service (root) is already connected");
                }
                _rootServiceConnected = true;
                _rootService = this;
            }
            else
            {
                if (_serviceConnected && !TerminateIfGone(_service))
                {
                    throw new InvalidOperationException("Plugin service is already connected");
                }
                _serviceConnected = true;
                _service = this;
                Connection.UnixSocket.GetPeerCredentials(out int servicePid, out _, out _);
                ServicePid = servicePid;
            }
        }

        // Start the plugins when both services are connected
        if (!_settings.UpdateOnly && _serviceConnected && _rootServiceConnected)
        {
            // First ensure that object model is up-to-date
            await _model.WaitForFullUpdateAsync(cancellationToken);

            Commands.StartPlugins startCommand = _commandFactory.Create<Commands.StartPlugins>();
            _ = Task.Run(async () => await startCommand.ExecuteAsync(), cancellationToken);
        }

        // Process incoming requests
        Queue<Tuple<BaseCommand, TaskCompletionSource<object?>>> pendingCommands = Connection.IsRoot ? _pendingRootCommands : _pendingCommands;
        try
        {
            do
            {
                // Wait for the next request and read it
                Tuple<BaseCommand, TaskCompletionSource<object?>>? request;
                using (await monitor.EnterAsync(cancellationToken))
                {
                    if (!pendingCommands.TryDequeue(out request))
                    {
                        await monitor.WaitAsync(cancellationToken);
                        if (!pendingCommands.TryDequeue(out request))
                        {
                            continue;
                        }
                    }
                }

                // Send it over to the plugin service. Exception logging should take place in the command processor
                try
                {
                    if (request.Item1 is ResolvePluginProcess)
                    {
                        string? result = await Connection.PerformCommandAsync<string>(request.Item1, cancellationToken);
                        request.Item2.SetResult(result);
                    }
                    else
                    {
                        await Connection.SendCommandAsync(request.Item1);
                        BaseResponse response = await Connection.ReceiveResponseAsync(cancellationToken);
                        if (response is ErrorResponse errorResponse)
                        {
                            request.Item2.SetException(new InternalServerException(request.Item1.Command, errorResponse.ErrorType, errorResponse.ErrorMessage));
                        }
                        else
                        {
                            request.Item2.SetResult(null);
                        }
                    }
                }
                catch (SocketException se)
                {
                    if (request.Item1 is Commands.StopPlugins)
                    {
                        // Service may terminate before our own request is fully processed
                        request.Item2.SetResult(null);
                    }
                    else
                    {
                        // Unexpected exception
                        request.Item2.SetException(se);
                    }
                }
                catch (Exception e)
                {
                    // Unexpected exception
                    request.Item2.SetException(e);
                }
            }
            while (!cancellationToken.IsCancellationRequested);
        }
        finally
        {
            // Do not use the cancellation token here, it is already cancelled on application shutdown and
            // the cleanup must still run to completion
            using (await monitor.EnterAsync())
            {
                // Plugins from this service are no longer running
                using (await _model.AccessReadWriteAsync(CancellationToken.None))
                {
                    foreach (Plugin item in _model.Plugins.Values)
                    {
                        if (item.Pid > 0 && item.SbcPermissions.HasFlag(SbcPermissions.SuperUser) == Connection.IsRoot)
                        {
                            item.Pid = 0;
                            item.Started = false;
                        }
                    }
                }

                // Service is no longer available unless another plugin service has taken over this registration
                bool superseded = !ReferenceEquals(Connection.IsRoot ? _rootService : _service, this);
                bool stopPlugins = !superseded && !_settings.UpdateOnly && _serviceConnected && _rootServiceConnected;
                if (!superseded)
                {
                    if (Connection.IsRoot)
                    {
                        _rootServiceConnected = false;
                        _rootService = null;
                    }
                    else
                    {
                        _serviceConnected = false;
                        _service = null;
                        ServicePid = 0;
                    }

                    // Invalidate pending requests
                    while (pendingCommands.TryDequeue(out Tuple<BaseCommand, TaskCompletionSource<object?>>? request))
                    {
                        request.Item2.TrySetCanceled();
                    }
                }

                // Stop the remaining plugins again unless they are already stopped
                if (stopPlugins)
                {
                    Commands.StopPlugins stopCommand = _commandFactory.Create<Commands.StopPlugins>();
                    _ = Task.Run(async () => await stopCommand.ExecuteAsync(cancellationToken), cancellationToken);
                }

                // Plugins from this service are no longer running
                using (await _model.AccessReadWriteAsync(CancellationToken.None))
                {
                    foreach (Plugin item in _model.Plugins.Values)
                    {
                        if (item.Pid > 0 && item.SbcPermissions.HasFlag(SbcPermissions.SuperUser) == Connection.IsRoot)
                        {
                            item.Pid = -1;
                            item.Started = false;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Check if the plugin service holding a registration is gone and terminate its processor if it is
    /// </summary>
    /// <param name="service">Processor of the registered plugin service</param>
    /// <returns>True if the registration has been released for another plugin service</returns>
    private static bool TerminateIfGone(PluginService? service)
    {
        if (service is not null)
        {
            try
            {
                service.Connection.Poll();
                return false;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                // Peer is gone, so the stale processor may be terminated
            }
            service._terminationCts.Cancel();
        }
        return true;
    }
}
