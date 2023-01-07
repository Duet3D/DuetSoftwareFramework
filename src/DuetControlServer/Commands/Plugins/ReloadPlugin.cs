using DuetAPI.ObjectModel;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.ReloadPlugin"/> command
    /// </summary>
    public sealed class ReloadPlugin : DuetAPI.Commands.ReloadPlugin
    {
        /// <summary>
        /// Start a plugin
        /// </summary>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="ArgumentException">Plugin is invalid</exception>
        public override async Task Execute()
        {
            if (!Settings.PluginSupport)
            {
                throw new NotSupportedException("Plugin support has been disabled");
            }

            using (await Model.Provider.AccessReadWriteAsync())
            {
                // The plugin must be stopped at this point
                if (Model.Provider.Get.Plugins.TryGetValue(Plugin, out Plugin plugin) && plugin.Pid > 0)
                {
                    throw new ArgumentException("Plugin must be stopped before its manifest can be reloaded");
                }

                // Update the plugin manifest
                string file = Path.Combine(Settings.PluginDirectory, Plugin + ".json");
                if (File.Exists(file))
                {
                    if (plugin is null)
                    {
                        plugin = new();
                        Model.Provider.Get.Plugins.Add(Plugin, plugin);
                    }

                    await using FileStream manifestStream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, Settings.FileBufferSize);
                    using JsonDocument manifestJson = await JsonDocument.ParseAsync(manifestStream);
                    plugin.UpdateFromJson(manifestJson.RootElement, false);
                    plugin.Pid = -1;
                }
                else
                {
                    if (plugin is null)
                    {
                        // Don't attempt to remove a non-existent plugin
                        return;
                    }
                    Model.Provider.Get.Plugins.Remove(Plugin);
                }
            }

            // Reload the plugin via the plugin services
            await IPC.Processors.PluginService.PerformCommand(this, true);
            await IPC.Processors.PluginService.PerformCommand(this, false);
        }
    }
}

