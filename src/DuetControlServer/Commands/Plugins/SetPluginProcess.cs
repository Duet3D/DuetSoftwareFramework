using DuetAPI.ObjectModel;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.InstallPlugin"/> command
    /// </summary>
    public sealed class SetPluginProcess : DuetAPI.Commands.SetPluginProcess
    {
        /// <summary>
        /// Update the pid of a given plugin
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override async Task Execute()
        {
            if (!Settings.PluginSupport)
            {
                throw new NotSupportedException("Plugin support has been disabled");
            }

            using (await Model.Provider.AccessReadWriteAsync())
            {
                if (Model.Provider.Get.Plugins.TryGetValue(Plugin, out Plugin plugin))
                {
                    plugin.Pid = Pid;
                }
                else
                {
                    throw new ArgumentException($"Plugin {Plugin} not found");
                }
            }
        }
    }
}
