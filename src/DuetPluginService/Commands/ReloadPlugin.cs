using DuetAPI.ObjectModel;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace DuetPluginService.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.ReloadPlugin"/> command
    /// </summary>
    public sealed class ReloadPlugin : DuetAPI.Commands.ReloadPlugin
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
                Plugin? plugin = null;
                foreach (Plugin item in Plugins.List)
                {
                    if (item.Id == Plugin)
                    {
                        plugin = item;
                        break;
                    }
                }

                // Update the plugin manifest
                string file = Path.Combine(Settings.PluginDirectory, Plugin + ".json");
                if (File.Exists(file))
                {
                    if (plugin is null)
                    {
                        plugin = new();
                        Plugins.List.Add(plugin);
                    }

                    await using FileStream manifestStream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
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
                    Plugins.List.Remove(plugin);
                }
            }
        }
    }
}
