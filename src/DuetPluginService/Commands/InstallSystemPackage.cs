using DuetAPIClient;
using DuetSharedLibrary;
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

namespace DuetPluginService.IPC;

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
    /// Transient unit that package installations run in
    /// </summary>
    /// <remarks>
    /// A package may upgrade DuetPluginService itself, and stopping that service takes its whole
    /// process tree with it, so the installation has to run outside it
    /// </remarks>
    private const string PackageUnit = "dsf-package";

    /// <summary>
    /// Overall progress once the package has been unpacked
    /// </summary>
    private const float UnpackedProgress = 0.1f;

    /// <summary>
    /// Overall progress once the packages have been installed and only the update script is left
    /// </summary>
    private const float UpdateScriptProgress = 0.9f;

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

        async Task ReportUpdateStatusAsync(string message, float progress)
        {
            using CommandConnection commandConnection = new();
            await commandConnection.ConnectAsync(_settings.SocketPath, cancellationToken);
            await commandConnection.SetUpdateStatusAsync(message, progress, cancellationToken);
        }

        string? packageDirectory = null, args;
        if (await IsZipFile(PackageFile, cancellationToken))
        {
            // Go into update mode, this may take longer
            logger.LogInformation("Start of combined ZIP package installation");
            await ReportUpdateStatusAsync("Unpacking package", 0f);

            // Unpack the ZIP file first
            packageDirectory = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(PackageFile));
            Directory.CreateDirectory(packageDirectory);
            ZipFile.ExtractToDirectory(PackageFile, packageDirectory);

            // Assemble the arguments
            string packageFiles = string.Join(' ', Directory.GetFiles(packageDirectory).Where(file => Path.GetFileName(file) != "update.sh"));
            args = _settings.InstallLocalPackageArguments.Replace("{file}", packageFiles);
            await ReportUpdateStatusAsync("Installing packages", UnpackedProgress);
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
                using Process process = TransientUnit.Start(PackageUnit, _settings.InstallLocalPackageCommand, args);
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
                    await ReportUpdateStatusAsync("Running update script", UpdateScriptProgress);
                    File.SetUnixFileMode(updateScript,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);

                    try
                    {
                        using Process updateScriptProcess = TransientUnit.Start(PackageUnit, updateScript, string.Empty);
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
                await commandConnection.ConnectAsync(_settings.SocketPath, cancellationToken);
                await commandConnection.SetUpdateStatusAsync(false, cancellationToken);
            }
            else
            {
                logger.LogInformation("End of package installation");
            }
        }
    }
}
