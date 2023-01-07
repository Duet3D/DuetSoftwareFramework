using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.IPC;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.SetPluginData"/> command
    /// </summary>
    public sealed class SetPluginData : DuetAPI.Commands.SetPluginData, IConnectionCommand
    {
        /// <summary>
        /// Source connection of this command
        /// </summary>
        public Connection? Connection { get; set; }

        /// <summary>
        /// Set custom plugin data in the object model
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override async Task Execute()
        {
            if (!Settings.PluginSupport)
            {
                throw new NotSupportedException("Plugin support has been disabled");
            }

            // Fill in plugin name if required
            if (string.IsNullOrEmpty(Plugin))
            {
                Plugin = Connection!.PluginId ?? throw new UnauthorizedAccessException("Failed to determine plugin ID");
            }

            // Check permissions. Only the owner or plugins with the ManagePlugins permission may modify plugin data
            if (Connection!.PluginId != Plugin && !Connection!.Permissions.HasFlag(SbcPermissions.ManagePlugins))
            {
                throw new UnauthorizedAccessException("Insufficient permissions");
            }

            // Update the plugin data
            using (await Model.Provider.AccessReadWriteAsync())
            {
                if (Model.Provider.Get.Plugins.TryGetValue(Plugin, out Plugin? plugin))
                {
                    if (!plugin.Data.ContainsKey(Key))
                    {
                        throw new ArgumentException($"Key {Key} not found in the plugin data");
                    }
                    plugin.Data[Key] = Value.Clone();        // create a clone so that the instance can be used even after the JsonDocument is disposed
                }
                else
                {
                    throw new ArgumentException($"Plugin {Plugin} not found");
                }
            }
        }
    }
}
