using DuetAPI.ObjectModel;
using System;
using System.Diagnostics;
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
            bool pluginFound = false;
            int pid = -1;
            using (await Model.Provider.AccessReadWriteAsync())
            {
                foreach (Plugin item in Model.Provider.Get.Plugins)
                {
                    if (item.Name == Plugin)
                    {
                        pid = item.Pid;
                        pluginFound = true;
                        break;
                    }
                }
            }

            if (!pluginFound)
            {
                throw new ArgumentException($"Plugin {Plugin} not found");
            }

            if (pid > 0)
            {
                // TODO If the process is running as super user, ask the elevation service to terminate it

                Process process = Process.GetProcessById(pid);
                if (process != null)
                {
                    // Ask process to terminate and wait a moment
                    LinuxApi.Commands.Kill(pid, LinuxApi.Signal.SIGTERM);
                    process.WaitForExit(4000);

                    // Kill it and any potential child processes
                    process.Kill(true);
                }
            }
        }
    }
}
