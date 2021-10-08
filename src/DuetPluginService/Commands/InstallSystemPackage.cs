using DuetAPIClient;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace DuetPluginService.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.InstallSystemPackage"/> command
    /// </summary>
    public sealed class InstallSystemPackage : DuetAPI.Commands.InstallSystemPackage
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private NLog.Logger _logger;

        /// <summary>
        /// Magic value every ZIP file starts with
        /// </summary>
        private static readonly byte[] ZipSignature = new byte[] { 0x50, 0x4B, 0x03, 0x04 };

        /// <summary>
        /// Check if the given file is a ZIP file
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        private static async Task<bool> IsZipFile(string fileName)
        {
            using FileStream fs = new(fileName, FileMode.Open, FileAccess.Read);
            byte[] firstBytes = new byte[ZipSignature.Length];

            if (await fs.ReadAsync(firstBytes, Program.CancellationToken) == ZipSignature.Length)
            {
                return ZipSignature.SequenceEqual(firstBytes);
            }
            return false;
        }

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
            _logger = NLog.LogManager.GetLogger(Path.GetFileName(PackageFile));

            string packageDirectory = null, args;
            if (await IsZipFile(PackageFile))
            {
                // Go into update mode, this may take longer
                _logger.Info("Start of combined ZIP package installation");
                using (CommandConnection commandConnection = new())
                {
                    await commandConnection.Connect(Settings.SocketPath);
                    await commandConnection.SetUpdateStatus(true);
                }

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
                _logger.Info("Start of package installation");
                args = Settings.InstallLocalPackageArguments.Replace("{file}", PackageFile);
            }

            try
            {
                // Run the installation process
                using Process process = Process.Start(Settings.InstallLocalPackageCommand, args);
                try
                {
                    await process.WaitForExitAsync(Program.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                    if (Program.CancelSource.IsCancellationRequested)
                    {
                        // Probably updating DPS as well so stop here
                        return;
                    }
                    throw;
                }

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
            finally
            {
                // Restore the previous update state if applicable
                if (packageDirectory != null)
                {
                    _logger.Info("End of combined ZIP package installation");
                    using CommandConnection commandConnection = new();
                    await commandConnection.Connect(Settings.SocketPath);
                    await commandConnection.SetUpdateStatus(false);
                }
                else
                {
                    _logger.Info("End of package installation");
                }
            }
        }
    }
}
