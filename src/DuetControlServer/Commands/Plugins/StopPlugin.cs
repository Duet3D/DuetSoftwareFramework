using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using System;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
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
            bool pluginFound = false, stopPlugin = false, asRoot = false;
            using (await Model.Provider.AccessReadWriteAsync())
            {
                foreach (Plugin item in Model.Provider.Get.Plugins)
                {
                    if (item.Name == Plugin)
                    {
                        pluginFound = true;
                        if (item.Pid > 0)
                        {
                            // Make sure no other running plugin depends on it
                            if (!StoppingAll)
                            {
                                foreach (Plugin other in Model.Provider.Get.Plugins)
                                {
                                    if (other.Name != Plugin && other.Pid > 0 && other.SbcPluginDependencies.Contains(Plugin))
                                    {
                                        throw new ArgumentException($"Cannot stop plugin because plugin {other.Name} depends on it");
                                    }
                                }
                            }

                            // Stop the plugin
                            item.Pid = 0;
                            stopPlugin = true;
                            asRoot = item.SbcPermissions.HasFlag(SbcPermissions.SuperUser);
                        }
                        return;
                    }
                }
            }

            if (!pluginFound)
            {
                throw new ArgumentException($"Plugin {Plugin} not found");
            }

            if (stopPlugin)
            {
                // Stop it via the plugin service. This will reset the PID to -1 too
                await IPC.Processors.PluginService.PerformCommand(this, asRoot);
            }
        }

        [JsonIgnore]
        public bool StoppingAll { get; set; }
    }
}
