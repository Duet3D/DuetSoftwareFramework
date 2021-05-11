using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace DuetPluginService.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.InstallSystemPackage"/> command
    /// </summary>
    public sealed class InstallSystemPackage : DuetAPI.Commands.InstallSystemPackage
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

            string args = Settings.InstallLocalPackageArguments.Replace("{file}", PackageFile);
            using Process process = Process.Start(Settings.InstallLocalPackageCommand, args);
            await process.WaitForExitAsync(Program.CancellationToken);

            if (process.ExitCode != 0)
            {
                throw new ArgumentException($"Failed to install system package (exit code {process.ExitCode})");
            }
        }
    }
}
