using System;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.UninstallSystemPackage"/> command
    /// </summary>
    public sealed class UninstallSystemPackage : DuetAPI.Commands.UninstallSystemPackage
    {
        /// <summary>
        /// Uninstall a system package
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="ArgumentException">Package could not be uninstalled</exception>
        public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
        {
            if (!Settings.RootPluginSupport)
            {
                throw new NotSupportedException("Root plugin support has been disabled");
            }

            // Forward this command to the plugin services
            await IPC.Processors.PluginService.PerformCommandAsync(this, true, cancellationToken);
        }
    }
}
