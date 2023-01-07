using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.IPC;
using System;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.UninstallPlugin"/> command
    /// </summary>
    public sealed class UninstallPlugin : DuetAPI.Commands.UninstallPlugin, IConnectionCommand
    {
        /// <summary>
        /// Internal flag to indicate that custom plugin files should not be purged
        /// </summary>
        public bool ForUpgrade { get; set; }

        /// <summary>
        /// Client connection
        /// </summary>
        [JsonIgnore]
        public Connection? Connection { get; set; }

        /// <summary>
        /// Uninstall a plugin
        /// </summary>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="ArgumentException">Plugin is invalid</exception>
        public override async Task Execute()
        {
            if (!Settings.PluginSupport)
            {
                throw new NotSupportedException("Plugin support has been disabled");
            }

            // Make sure the upgrade switch is only used by the plugin service
            if (ForUpgrade && (Connection is not null && !Connection.Permissions.HasFlag(SbcPermissions.ServicePlugins)))
            {
                throw new ArgumentException($"{nameof(ForUpgrade)} switch must not be used by third-party applications");
            }

            // Find the plugin to uninstall
            Plugin plugin;
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                if (Model.Provider.Get.Plugins.TryGetValue(Plugin, out plugin))
                {
                    // Make sure no other plugin depends on this plugin
                    foreach (Plugin item in Model.Provider.Get.Plugins.Values)
                    {
                        if (item.Id != Plugin && (item.DwcDependencies.Contains(Plugin) || item.SbcPluginDependencies.Contains(Plugin)))
                        {
                            throw new ArgumentException($"Cannot uninstall plugin because plugin {item.Id} depends on it");
                        }
                    }
                }
                else
                {
                    throw new ArgumentException($"Plugin {Plugin} not found");
                }
            }

            // Stop it if required
            StopPlugin stopCommand = new()
            {
                Plugin = Plugin
            };
            await IPC.Processors.PluginService.PerformCommand(stopCommand, plugin.SbcPermissions.HasFlag(SbcPermissions.SuperUser));

            // Make sure we don't attempt to start it again once it's uninstalled
            await using FileStream fileStream = new(Settings.PluginsFilename, FileMode.Create, FileAccess.Write);
            await using StreamWriter writer = new(fileStream);
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                foreach (Plugin item in Model.Provider.Get.Plugins.Values)
                {
                    if (item.Pid >= 0 && item.Id != Plugin)
                    {
                        await writer.WriteLineAsync(item.Id);
                    }
                }
            }

            // Perform the actual uninstallation via the plugin service.
            // If it is a root plugin, the root plugin service will clean up everything
            await IPC.Processors.PluginService.PerformCommand(this, false);
            await IPC.Processors.PluginService.PerformCommand(this, true);

            // Reset it in the object model
            using (await Model.Provider.AccessReadWriteAsync())
            {
                Model.Provider.Get.Plugins.Remove(Plugin);
            }
        }
    }
}
