using DuetAPI.ObjectModel;
using LinuxDevices;
using System.Diagnostics;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.StopPlugin"/> command
    /// </summary>
    public class StopPlugin : DuetAPI.Commands.StopPlugin
    {
        /// <summary>
        /// Stop a plugin
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override async Task Execute()
        {
            int pid = -1;
            using (await Model.Provider.AccessReadWriteAsync())
            {
                foreach (Plugin item in Model.Provider.Get.Plugins)
                {
                    if (item.Name == Plugin)
                    {
                        pid = item.PID;
                        break;
                    }
                }
            }

            if (pid > 0)
            {
                // TODO If the process is running as super user, ask the elevation service to terminate it

                Process process = Process.GetProcessById(pid);
                if (process != null)
                {
                    // Ask process to terminate and wait a moment
                    Signal.Kill(pid, Signal.SIGTERM);
                    process.WaitForExit(4000);

                    // Kill it and any potential child processes
                    process.Kill(true);
                }
            }
        }
    }
}
