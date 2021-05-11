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

            // Forward this command to the plugin services
            await IPC.Processors.PluginService.PerformCommand(this, true);
        }
    }
}
