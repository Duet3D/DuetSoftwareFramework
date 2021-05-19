using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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

            string packageDirectory = null, args;
            if (PackageFile.EndsWith(".zip", StringComparison.InvariantCultureIgnoreCase))
            {
                // Unpack the ZIP file first
                packageDirectory = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(PackageFile));
                Directory.CreateDirectory(packageDirectory);
                ZipFile.ExtractToDirectory(PackageFile, packageDirectory);

                // Assemble the arguments
                args = Settings.InstallLocalPackageArguments.Replace("{file}", string.Join(' ', Directory.GetFiles(packageDirectory)));
            }
            else
            {
                // Just need to install a single file
                args = Settings.InstallLocalPackageArguments.Replace("{file}", PackageFile);
            }

            // Run the installation process
            using Process process = Process.Start(Settings.InstallLocalPackageCommand, args);
            await process.WaitForExitAsync(Program.CancellationToken);

            // Clean up again
            if (packageDirectory != null)
            {
                Directory.Delete(packageDirectory, true);
            }

            // Check the installation result
            if (process.ExitCode != 0)
            {
                throw new ArgumentException($"Failed to install system package (exit code {process.ExitCode})");
            }
        }
    }
}
