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
            using (await Model.Provider.AccessReadWriteAsync())
            {
                foreach (Plugin item in Model.Provider.Get.Plugins)
                {
                    if (item.Name == Plugin)
                    {
                        item.Pid = Pid;
                        return;
                    }
                }
            }
            throw new ArgumentException($"Plugin {Plugin} not found");
        }
    }
}
