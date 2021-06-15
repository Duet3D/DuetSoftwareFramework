using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetAPIClient;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.IPC.Processors
{
    /// <summary>
    /// IPC processor for plugin services
    /// </summary>
    public sealed class PluginService : Base
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
        /// Queue of pending service commands vs tasks
        /// </summary>
        private static readonly Queue<Tuple<object, TaskCompletionSource>> _pendingCommands = new();

        /// <summary>
        /// Queue of pending service commands vs tasks
        /// </summary>
        private static readonly Queue<Tuple<object, TaskCompletionSource>> _pendingRootCommands = new();

        /// <summary>
        /// Perform a command via the plugin service
        /// </summary>
        /// <param name="command">Command to perform</param>
        /// <param name="asRoot">Send it to the service running as root</param>
        /// <returns>Asynchronous task</returns>
        public static async Task PerformCommand(object command, bool asRoot)
        {
            TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (await (asRoot ? _rootMonitor : _monitor).EnterAsync(Program.CancellationToken))
            {
                if (asRoot)
                {
                    if (_rootServiceConnected)
                    {
                        _pendingRootCommands.Enqueue(new Tuple<object, TaskCompletionSource>(command, tcs));
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
                        _pendingCommands.Enqueue(new Tuple<object, TaskCompletionSource>(command, tcs));
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
        /// Constructor of the plugin runner proxy processor
        /// </summary>
        /// <param name="conn">Connection instance</param>
        public PluginService(Connection conn) : base(conn) => conn.Logger.Debug("PluginService processor added");

        /// <summary>
        /// Handles the remote connection
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override async Task Process()
        {
            if (!Settings.PluginSupport)
            {
                throw new NotSupportedException("Plugin support has been disabled");
            }

            // Try to register this plugin service
            AsyncMonitor monitor = Connection.IsRoot ? _rootMonitor : _monitor;
            using (await monitor.EnterAsync(Program.CancellationToken))
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
            if (!Settings.UpdateOnly && _serviceConnected && _rootServiceConnected)
            {
                // First ensure that object model is up-to-date
                await Model.Updater.WaitForFullUpdate();

                Commands.StartPlugins startCommand = new();
                _ = Task.Run(startCommand.Execute);
            }

            // Process incoming requests
            Queue<Tuple<object, TaskCompletionSource>> pendingCommands = Connection.IsRoot ? _pendingRootCommands : _pendingCommands;
            try
            {
                do
                {
                    // Wait for the next request and read it
                    Tuple<object, TaskCompletionSource> request;
                    try
                    {
                        using (await monitor.EnterAsync(Program.CancellationToken))
                        {
                            if (!pendingCommands.TryDequeue(out request))
                            {
                                using CancellationTokenSource timeoutCts = new(Settings.SocketPollInterval);
                                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, Program.CancellationToken);
                                await monitor.WaitAsync(cts.Token);
                                request = pendingCommands.Dequeue();
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!Program.CancellationToken.IsCancellationRequested)
                    {
                        Connection.Poll();
                        continue;
                    }

                    // Send it over to the plugin service. Exception logging should take place in the command processor
                    try
                    {
                        await Connection.Send(request.Item1);
                        BaseResponse response = await Connection.ReceiveResponse();
                        if (response is ErrorResponse errorResponse)
                        {
                            // Failed to process request, propagate the error
                            string command = ((BaseCommand)request.Item1).Command;
                            request.Item2.SetException(new InternalServerException(command, errorResponse.ErrorType, errorResponse.ErrorMessage));
                        }
                        else
                        {
                            // Command successfully executed
                            request.Item2.SetResult();
                        }
                    }
                    catch (Exception e)
                    {
                        // Unexpected exception
                        request.Item2.SetException(e);
                    }
                }
                while (!Program.CancellationToken.IsCancellationRequested);
            }
            finally
            {
                using (await monitor.EnterAsync())
                {
                    // Plugins from this service are no longer running
                    using (await Model.Provider.AccessReadWriteAsync())
                    {
                        foreach (Plugin item in Model.Provider.Get.Plugins.Values)
                        {
                            if (item.Pid > 0 && item.SbcPermissions.HasFlag(SbcPermissions.SuperUser) == Connection.IsRoot)
                            {
                                item.Pid = -1;
                            }
                        }
                    }

                    // Service is no longer available
                    bool stopPlugins = !Settings.UpdateOnly && _serviceConnected && _rootServiceConnected;
                    if (Connection.IsRoot)
                    {
                        _rootServiceConnected = false;
                    }
                    else
                    {
                        _serviceConnected = false;
                    }

                    // Invalidate pending requests
                    while (pendingCommands.TryDequeue(out Tuple<object, TaskCompletionSource> request))
                    {
                        request.Item2.SetCanceled();
                    }

                    // Stop the remaining plugins again unless they are already stopped
                    if (stopPlugins)
                    {
                        Commands.StopPlugins stopCommand = new();
                        _ = Task.Run(stopCommand.Execute);
                    }
                }
            }
        }
    }
}
