using System;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.InstallSystemPackage"/> command
    /// </summary>
    public sealed class InstallSystemPackage : DuetAPI.Commands.InstallSystemPackage
    {
        /// <summary>
        /// Install or upgrade a system package
        /// </summary>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="ArgumentException">Package could not be installed</exception>
        public override async Task Execute()
        {
            if (!Settings.RootPluginSupport)
            {
                throw new NotSupportedException("Root plugin support has been disabled");
            }

            // It is compulsory to stop the plugins before system packages are installed.
            // This is required to avoid deadlocks when M997 is called by the reprapfirmware package
            StopPlugins stopCommand = new();
            await stopCommand.Execute();

            try
            {
                // Forward this command to the plugin services
                await IPC.Processors.PluginService.PerformCommand(this, true);
            }
            catch (OperationCanceledException)
            {
                // This exception can be expected when RRF has been updated
                if (Settings.NoTerminateOnReset)
                {
                    throw;
                }
            }
        }
    }
}
