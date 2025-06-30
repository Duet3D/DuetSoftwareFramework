using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection.InitMessages;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Commands;
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
public sealed class PluginService : IProcessor
{
    /// <summary>
    /// Logger instance
    /// </summary>
    private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

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
    /// Queue of pending service commands vs tasks
    /// </summary>
    private static readonly Queue<Tuple<BaseCommand, TaskCompletionSource>> _pendingCommands = new();

    /// <summary>
    /// Queue of pending service commands vs tasks
    /// </summary>
    private static readonly Queue<Tuple<BaseCommand, TaskCompletionSource>> _pendingRootCommands = new();

    /// <summary>
    /// Perform a command via the plugin service
    /// </summary>
    /// <param name="command">Command to perform</param>
    /// <param name="asRoot">Send it to the service running as root</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public static async Task PerformCommandAsync(BaseCommand command, bool asRoot, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using (await (asRoot ? _rootMonitor : _monitor).EnterAsync(cancellationToken))
        {
            if (asRoot)
            {
                if (_rootServiceConnected)
                {
                    _pendingRootCommands.Enqueue(new Tuple<BaseCommand, TaskCompletionSource>(command, tcs));
                    _rootMonitor.Pulse();
                }
                else
                {
                    throw new InvalidOperationException("Cannot perform command because the plugin service (root) is not started");
                }
            }
            else
            {
                if (_serviceConnected)
                {
                    _pendingCommands.Enqueue(new Tuple<BaseCommand, TaskCompletionSource>(command, tcs));
                    _monitor.Pulse();
                }
                else
                {
                    throw new InvalidOperationException("Cannot perform command because the plugin service is not started");
                }
            }
        }
        await tcs.Task;
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
    /// Constructor of the plugin runner proxy processor
    /// </summary>
    /// <param name="conn">Connection instance</param>
    /// <param name="initMessage">Initialization message from the client</param>
    /// <param name="commandFactory">Command factory to create commands</param>
    /// <param name="model">Object model instance</param>
    /// <param name="updater">Object model updater</param>
    /// <param name="settings">Settings</param>
    public PluginService(Connection conn, ClientInitMessage initMessage, CommandFactory commandFactory, Model.ObjectModel model, IOptions<Settings> settings)
    {
        Connection = conn;
        _commandFactory = commandFactory;
        _model = model;
        _settings = settings.Value;

        _logger.Debug("PluginService processor added for IPC#{0}", conn.Id);
    }

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

        // Try to register this plugin service
        AsyncMonitor monitor = Connection.IsRoot ? _rootMonitor : _monitor;
        using (await monitor.EnterAsync(cancellationToken))
        {
            if (Connection.IsRoot)
            {
                if (_rootServiceConnected)
                {
                    throw new InvalidOperationException("Plugin service (root) is already connected");
                }
                _rootServiceConnected = true;
            }
            else
            {
                if (_serviceConnected)
                {
                    throw new InvalidOperationException("Plugin service is already connected");
                }
                _serviceConnected = true;
            }
        }

        // Start the plugins when both services are connected
        if (!_settings.UpdateOnly && _serviceConnected && _rootServiceConnected)
        {
            // First ensure that object model is up-to-date
            await Model.Updater.WaitForFullUpdateAsync(cancellationToken);

            Commands.StartPlugins startCommand = _commandFactory.Create<Commands.StartPlugins>();
            _ = Task.Run(async () => await startCommand.ExecuteAsync(), cancellationToken);
        }

        // Process incoming requests
        Queue<Tuple<BaseCommand, TaskCompletionSource>> pendingCommands = Connection.IsRoot ? _pendingRootCommands : _pendingCommands;
        try
        {
            do
            {
                // Wait for the next request and read it
                Tuple<BaseCommand, TaskCompletionSource>? request;
                try
                {
                    using (await monitor.EnterAsync(cancellationToken))
                    {
                        if (!pendingCommands.TryDequeue(out request))
                        {
                            using CancellationTokenSource timeoutCts = new(_settings.SocketPollInterval);
                            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
                            await monitor.WaitAsync(cts.Token);
                            request = pendingCommands.Dequeue();
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Connection.Poll();
                    continue;
                }

                // Send it over to the plugin service. Exception logging should take place in the command processor
                try
                {
                    await Connection.SendCommand(request.Item1);
                    BaseResponse response = await Connection.ReceiveResponseAsync(cancellationToken);
                    if (response is ErrorResponse errorResponse)
                    {
                        // Failed to process request, propagate the error
                        string command = request.Item1.Command;
                        request.Item2.SetException(new InternalServerException(command, errorResponse.ErrorType, errorResponse.ErrorMessage));
                    }
                    else
                    {
                        // Command successfully executed
                        request.Item2.SetResult();
                    }
                }
                catch (SocketException se)
                {
                    if (request.Item1 is Commands.StopPlugins)
                    {
                        // Service may terminate before our own request is fully processed
                        request.Item2.SetResult();
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
            using (await monitor.EnterAsync(cancellationToken))
            {
                // Plugins from this service are no longer running
                using (await _model.AccessReadWriteAsync(cancellationToken))
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

                // Service is no longer available
                bool stopPlugins = !_settings.UpdateOnly && _serviceConnected && _rootServiceConnected;
                if (Connection.IsRoot)
                {
                    _rootServiceConnected = false;
                }
                else
                {
                    _serviceConnected = false;
                }

                // Invalidate pending requests
                while (pendingCommands.TryDequeue(out Tuple<BaseCommand, TaskCompletionSource>? request))
                {
                    request.Item2.SetCanceled(cancellationToken);
                }

                // Stop the remaining plugins again unless they are already stopped
                if (stopPlugins)
                {
                    Commands.StopPlugins stopCommand = _commandFactory.Create<Commands.StopPlugins>();
                    _ = Task.Run(async () => await stopCommand.ExecuteAsync(cancellationToken), cancellationToken);
                }

                // Plugins from this service are no longer running
                using (await _model.AccessReadWriteAsync(cancellationToken))
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
}
