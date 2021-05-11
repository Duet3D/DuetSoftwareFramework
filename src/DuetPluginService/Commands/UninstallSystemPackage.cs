using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace DuetPluginService.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.UninstallSystemPackage"/> command
    /// </summary>
    public sealed class UninstallSystemPackage : DuetAPI.Commands.UninstallSystemPackage
    {
        /// <summary>
        /// Uninstall a system package
        /// </summary>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="ArgumentException">Failed to uninstall package</exception>
        public override async Task Execute()
        {
            if (!Program.IsRoot)
            {
                throw new ArgumentException("Unable to manage system packages without root privileges");
            }

            string args = Settings.UninstallLocalPackageArguments.Replace("{package}", Package);
            using Process process = Process.Start(Settings.UninstallLocalPackageCommand, args);
            await process.WaitForExitAsync(Program.CancellationToken);

            if (process.ExitCode != 0)
            {
                throw new ArgumentException($"Failed to uninstall system package (exit code {process.ExitCode})");
            }
        }
    }
}
