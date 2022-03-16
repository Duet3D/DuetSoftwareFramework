using DuetAPI.ObjectModel;
using Nito.AsyncEx;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.StopPlugins"/> command
    /// </summary>
    public sealed class StopPlugins : DuetAPI.Commands.StopPlugins
    {
        /// <summary>
        /// Indicates if the plugins are being started
        /// </summary>
        private static readonly AsyncLock _stopLock = new();

        /// <summary>
        /// Stop all the plugins
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override async Task Execute()
        {
            if (!Settings.PluginSupport)
            {
                return;
            }

            using (await _stopLock.LockAsync(Program.CancellationToken))
            {
                // Don't proceed if all the plugins have been stopped
                using (await Model.Provider.AccessReadOnlyAsync())
                {
                    if (!Model.Provider.Get.State.PluginsStarted)
                    {
                        return;
                    }
                }

                // Stop all plugins
                StringBuilder startedPlugins = new();
                List<Task> stopTasks = new();
                using (await Model.Provider.AccessReadOnlyAsync())
                {
                    foreach (Plugin item in Model.Provider.Get.Plugins.Values)
                    {
                        if (item.Pid >= 0)
                        {
                            startedPlugins.AppendLine(item.Id);

                            if (item.Pid > 0)
                            {
                                StopPlugin stopCommand = new()
                                {
                                    Plugin = item.Id,
                                    SaveState = false,
                                    StoppingAll = true
                                };
                                stopTasks.Add(stopCommand.Execute());
                            }
                        }
                    }
                }

                try
                {
                    await Task.WhenAll(stopTasks);
                }
                catch (SocketException)
                {
                    // Can be expected when the remote service is terminated too early
                }

                using (await Model.Provider.AccessReadWriteAsync())
                {
                    // Plugins have been stopped
                    Model.Provider.Get.State.PluginsStarted = false;
                }
            }
        }
    }
}
