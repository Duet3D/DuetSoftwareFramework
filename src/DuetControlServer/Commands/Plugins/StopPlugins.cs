using DuetAPI.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.StopPlugins"/> command
    /// </summary>
    public sealed class StopPlugins : DuetAPI.Commands.StopPlugins
    {
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

            using (await Model.Provider.AccessReadOnlyAsync())
            {
                if (!Model.Provider.Get.State.PluginsStarted)
                {
                    // No plugins started before, no need to save the execution state
                    return;
                }
            }

            List<Task> stopTasks = new();
            using (FileStream fileStream = new(Settings.PluginsFilename, FileMode.Create, FileAccess.Write))
            {
                using StreamWriter writer = new(fileStream);
                using (await Model.Provider.AccessReadOnlyAsync())
                {
                    foreach (Plugin item in Model.Provider.Get.Plugins.Values)
                    {
                        if (item.Pid > 0)
                        {
                            await writer.WriteLineAsync(item.Id);

                            StopPlugin stopCommand = new() { Plugin = item.Id, StoppingAll = true };
                            stopTasks.Add(Task.Run(stopCommand.Execute));
                        }
                    }
                }
            }
            await Task.WhenAll(stopTasks);

            using (await Model.Provider.AccessReadWriteAsync())
            {
                // Plugins have been stopped
                Model.Provider.Get.State.PluginsStarted = false;
            }
        }
    }
}
