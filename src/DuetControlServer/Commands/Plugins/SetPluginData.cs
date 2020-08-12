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
    public class SetPluginData : DuetAPI.Commands.SetPluginData, IConnectionCommand
    {
        /// <summary>
        /// Set custom plugin data in the object model
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override async Task Execute()
        {
            // Fill in plugin name if required
            if (string.IsNullOrEmpty(Plugin))
            {
                Plugin = Connection.PluginName;
            }

            // Check permissions
            if (Connection.PluginName != Plugin && !Connection.Permissions.HasFlag(SbcPermissions.ManagePlugins))
            {
                throw new UnauthorizedAccessException("Insufficient permissions");
            }

            // Update the plugin data
            using (await Model.Provider.AccessReadWriteAsync())
            {
                foreach (Plugin plugin in Model.Provider.Get.Plugins)
                {
                    if (plugin.Name == Plugin)
                    {
                        plugin.Data[Key] = Value;
                        return;
                    }
                }
            }

            // Plugin not found
            throw new ArgumentException($"Plugin {Plugin} not found");
        }

        /// <summary>
        /// Source connection of this command
        /// </summary>
        public Connection Connection { get; set; }
    }
}
