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
            using (await Plugins.LockAsync())
            {
                // Try to find the plugin first
                Plugin plugin = null;
                foreach (Plugin item in Plugins.List)
                {
                    if (item.Name == Plugin)
                    {
                        plugin = item;
                        break;
                    }
                }
                if (plugin == null)
                {
                    throw new ArgumentException($"Plugin {Plugin} not found by {(Program.IsRoot ? "root service" : "service")}");
                }

                // Is this the right service to start the plugin?
                if (plugin.SbcPermissions.HasFlag(SbcPermissions.SuperUser) != Program.IsRoot)
                {
                    throw new InvalidOperationException("Wrong plugin service to start this plugin");
                }

                // Try to stop the process
                if (Plugins.Processes.TryGetValue(plugin.Name, out Process process) && !process.HasExited)
                {
                    // Ask process to terminate and wait a moment
                    LinuxApi.Commands.Kill(process.Id, LinuxApi.Signal.SIGTERM);
                    using (CancellationTokenSource timeoutCts = new CancellationTokenSource(Settings.StopTimeout))
                    {
                        // Do not link this CTS to the main CTS because we may be shutting down at this point
                        await process.WaitForExitAsync(timeoutCts.Token);
                    }

                    // Kill it and any potentially left-over child processes
                    process.Kill(true);
                }
            }
        }
    }
}
