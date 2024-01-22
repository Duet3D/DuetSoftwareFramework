using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DuetPluginService.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.StopPlugin"/> command
    /// </summary>
    public sealed class StopPlugin : DuetAPI.Commands.StopPlugin
    {
        /// <summary>
        /// Stop a plugin
        /// </summary>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="ArgumentException">Plugin is invalid</exception>
        public override async Task Execute()
        {
            NLog.Logger logger = NLog.LogManager.GetLogger($"Plugin {Plugin}");

            using (await Plugins.LockAsync())
            {
                // Try to find the plugin first
                Plugin? plugin = null;
                foreach (Plugin item in Plugins.List)
                {
                    if (item.Id == Plugin)
                    {
                        plugin = item;
                        break;
                    }
                }

                if (plugin is null)
                {
                    throw new ArgumentException($"Plugin {Plugin} not found by {(Program.IsRoot ? "root service" : "service")}");
                }

                // Is this the right service to start the plugin?
                if (plugin.SbcPermissions.HasFlag(SbcPermissions.SuperUser) != Program.IsRoot)
                {
                    throw new InvalidOperationException("Wrong plugin service to start this plugin");
                }

                // Try to stop the process
                if (Plugins.Processes.TryGetValue(plugin.Id, out Process? process) && !process.HasExited)
                {
                    try
                    {
                        // Ask process to terminate
                        logger.Info("Attempting to stop process (pid {0})...", process.Id);
                        LinuxApi.Commands.Kill(process.Id, LinuxApi.Signal.SIGTERM);

                        // Wait a moment. Do not link this CTS to the main CTS because we may be shutting down at this point
                        using CancellationTokenSource timeoutCts = new(Settings.StopTimeout);
                        await process.WaitForExitAsync(timeoutCts.Token);

                        // Process terminated
                        logger.Info("Process stopped by SIGTERM");
                    }
                    catch (OperationCanceledException)
                    {
                        // Kill it and any potentially left-over child processes
                        process.Kill(true);
                        logger.Info("Process killed");
                    }
                }
            }
        }
    }
}
