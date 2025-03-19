using DuetAPIClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace DuetPluginService.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.InstallSystemPackage"/> command
/// </summary>
/// <param name="loggerFactory">Logger factory</param>
/// <param name="settings">Application settings</param>
public sealed class InstallSystemPackage(ILoggerFactory loggerFactory, IOptions<Settings> settings) : DuetAPI.Commands.InstallSystemPackage
{
    private readonly Settings _settings = settings.Value;

    /// <summary>
    /// Magic value every ZIP file starts with
    /// </summary>
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];

    /// <summary>
    /// Check if the given file is a ZIP file
    /// </summary>
    /// <param name="fileName">File to check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Whether the file is a ZIP</returns>
    private static async Task<bool> IsZipFile(string fileName, CancellationToken cancellationToken)
    {
        await using FileStream fs = new(fileName, FileMode.Open, FileAccess.Read);
        byte[] firstBytes = new byte[ZipSignature.Length];

        if (await fs.ReadAsync(firstBytes, cancellationToken) == ZipSignature.Length)
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
    [UnsupportedOSPlatform("windows")]
    public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (!Environment.IsPrivilegedProcess)
        {
            throw new ArgumentException("Unable to manage system packages without root privileges");
        }
        ILogger logger = loggerFactory.CreateLogger($"Package {Path.GetFileName(PackageFile)}");

        string? packageDirectory = null, args;
        if (await IsZipFile(PackageFile, cancellationToken))
        {
            // Go into update mode, this may take longer
            logger.LogInformation("Start of combined ZIP package installation");
            using (CommandConnection commandConnection = new())
            {
                await commandConnection.Connect(_settings.SocketPath);
                await commandConnection.SetUpdateStatus(true);
            }

            // Unpack the ZIP file first
            packageDirectory = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(PackageFile));
            Directory.CreateDirectory(packageDirectory);
            ZipFile.ExtractToDirectory(PackageFile, packageDirectory);

            // Assemble the arguments
            string packageFiles = string.Join(' ', Directory.GetFiles(packageDirectory).Where(file => Path.GetFileName(file) != "update.sh"));
            args = _settings.InstallLocalPackageArguments.Replace("{file}", packageFiles);
        }
        else
        {
            // Just need to install a single file
            logger.LogInformation("Start of package installation");
            args = _settings.InstallLocalPackageArguments.Replace("{file}", PackageFile);
        }

        try
        {
            int exitCode = 0;
            if (!string.IsNullOrWhiteSpace(args))
            {
                // Run the installation process
                using Process process = Process.Start(_settings.InstallLocalPackageCommand, args);
                try
                {
                    await process.WaitForExitAsync(cancellationToken);
                    exitCode = process.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        // Probably updating DPS as well so stop here
                        return;
                    }
                    throw;
                }
            }

            if (packageDirectory is not null)
            {
                // Run update script if it exists
                string updateScript = Path.Combine(packageDirectory, "update.sh");
                if (File.Exists(updateScript))
                {
                    File.SetUnixFileMode(updateScript,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);

                    try
                    {
                        using Process updateScriptProcess = Process.Start(updateScript);
                        await updateScriptProcess.WaitForExitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            // Probably updating DPS as well so stop here
                            return;
                        }
                        throw;
                    }
                }

                // Clean up again
                Directory.Delete(packageDirectory, true);
            }

            // Check the installation result
            if (exitCode != 0)
            {
                throw new ArgumentException($"Failed to install system package (exit code {exitCode})");
            }
        }
        finally
        {
            // Restore the previous update state if applicable
            if (packageDirectory is not null)
            {
                logger.LogInformation("End of combined ZIP package installation");
                using CommandConnection commandConnection = new();
                await commandConnection.Connect(_settings.SocketPath, cancellationToken);
                await commandConnection.SetUpdateStatus(false, cancellationToken);
            }
            else
            {
                logger.LogInformation("End of package installation");
            }
        }
    }
}
