using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetAPIClient;
using DuetPluginService.PermissionManagers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DuetPluginService.IPC;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.InstallPlugin"/> command
/// </summary>
/// <param name="permissionManager">Permission manager</param>
/// <param name="pluginStore">Plugin store</param>
/// <param name="loggerFactory">Logger factory</param>
/// <param name="settings">Application settings</param>
public sealed class InstallPlugin(IPermissionManager permissionManager, PluginStore pluginStore, ILoggerFactory loggerFactory, IOptions<Settings> settings) : DuetAPI.Commands.InstallPlugin
{
    private readonly Settings _settings = settings.Value;

    /// <summary>
    /// Internal flag to indicate that custom plugin files should not be purged
    /// </summary>
    public bool Upgrade { get; set; }

    /// <summary>
    /// Install or upgrade a plugin
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="ArgumentException">Plugin installation failed</exception>
    [UnsupportedOSPlatform("windows")]
    public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // Extract the plugin manifest
        using ZipArchive zipArchive = ZipFile.OpenRead(PluginFile);
        Plugin plugin = await ExtractManifest(zipArchive);
        ILogger logger = loggerFactory.CreateLogger($"Plugin {plugin.Id}");

        // Obtain virtual SD path
        string sdPath;
        using (CommandConnection connection = new())
        {
            await connection.ConnectAsync(_settings.SocketPath, cancellationToken);
            sdPath = await connection.ResolvePathAsync("0:/", cancellationToken);
        }

        if (Environment.IsPrivilegedProcess)
        {
            // Run preinstall routine if needed
            if (plugin.SbcPackageDependencies.Count > 0 && !string.IsNullOrEmpty(_settings.PreinstallPackageCommand))
            {
                logger.LogInformation("Running preinstall command");
                using Process process = Process.Start(_settings.PreinstallPackageCommand, _settings.PreinstallPackageArguments);
                await process.WaitForExitAsync(cancellationToken);
            }

            // Install plugin dependencies
            foreach (string package in plugin.SbcPackageDependencies)
            {
                logger.LogInformation("Installing package {Package}", package);
                await InstallPackage(package, cancellationToken);
            }

            // Apply security profile for this plugin unless it gets root permissions anyway
            if (!plugin.SbcPermissions.HasFlag(SbcPermissions.SuperUser) && !_settings.DisableAppArmor)
            {

                // Install security profile
                await permissionManager.InstallProfileAsync(plugin, settings.Value.PluginDirectory, sdPath, cancellationToken);
                logger.LogInformation("Security profile installed");
            }
        }
        else
        {
            // Delete old files
            string pluginBase = Path.Combine(settings.Value.PluginDirectory, plugin.Id);
            if (!Upgrade && Directory.Exists(pluginBase))
            {
                try
                {
                    logger.LogWarning("Deleting previous installation directory");
                    Directory.Delete(pluginBase, true);
                }
                catch
                {
                    logger.LogError("Failed to remove previous installation directory {Directory}", pluginBase);
                    throw new ArgumentException($"Failed to remove previous installation directory {pluginBase}");
                }
            }

            // Clear file lists, they are assigned during the installation
            plugin.DsfFiles.Clear();
            plugin.DwcFiles.Clear();
            plugin.SdFiles.Clear();
            logger.LogInformation("Installing files");

            // Make plugin directory
            if (!Directory.Exists(pluginBase))
            {
                logger.LogDebug("Creating plugin base directory {Directory}", pluginBase);
                Directory.CreateDirectory(pluginBase);
            }

            // Install new plugin files
            string architecture = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm => "arm",
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                Architecture.X64 => "x86_64",
                _ => "unknown"
            };

            foreach (ZipArchiveEntry entry in zipArchive.Entries)
            {
                // Ignore plugin.json, it will be written when this archive has been extracted
                // Also ignore directories, they are automatically created below
                if (entry.FullName == "plugin.json" || entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                string fileName;
                string allowedBase;
                if (entry.FullName.StartsWith("dsf/"))
                {
                    // Put DSF plugin files into <PluginDirectory>/<PluginName>/dsf
                    fileName = Path.Combine(pluginBase, entry.FullName);
                    allowedBase = pluginBase;
                    plugin.DsfFiles.Add(entry.FullName[4..]);
                }
                else if (entry.FullName.StartsWith("dwc/"))
                {
                    // Put DWC plugin files into <PluginDirectory>/<PluginName>/dwc
                    fileName = Path.Combine(pluginBase, entry.FullName);
                    allowedBase = pluginBase;
                    plugin.DwcFiles.Add(entry.FullName[4..]);
                }
                else if (entry.FullName.StartsWith("sd/"))
                {
                    // Put SD files into 0:/
                    fileName = Path.Combine(sdPath, entry.FullName[3..]);
                    allowedBase = sdPath;
                    plugin.SdFiles.Add(entry.FullName[3..]);
                }
                else
                {
                    // Skip other files
                    logger.LogWarning("Skipping installation of file {File}", entry.FullName);
                    continue;
                }

                // Guard against zip-slip: a malicious archive could use '..' segments or absolute paths in entry names
                // to escape the plugin directory and overwrite files elsewhere
                string resolvedFileName = Path.GetFullPath(fileName);
                string resolvedBase = Path.GetFullPath(allowedBase);
                if (!resolvedFileName.StartsWith(resolvedBase + Path.DirectorySeparatorChar, StringComparison.Ordinal) && resolvedFileName != resolvedBase)
                {
                    throw new ArgumentException($"Refusing to install entry {entry.FullName}: path escapes plugin directory");
                }

                // Make sure the parent directory exists
                string parentDirectory = Path.GetDirectoryName(fileName)!;
                if (!Directory.Exists(parentDirectory))
                {
                    logger.LogDebug("Creating new directory {Directory}", parentDirectory);
                    Directory.CreateDirectory(parentDirectory);
                }

                // Extract the file
                if (File.Exists(fileName) && plugin.SbcConfigFiles.Any(file => fileName == Path.Combine(sdPath, "sys", file) || fileName == Path.Combine(sdPath, file)))
                {
                    logger.LogDebug("Not overwriting config file {File}", entry.FullName);
                }
                else
                {
                    logger.LogDebug("Extracting {CompressedFile} to {File}", entry.FullName, fileName);
                    await using FileStream fileStream = new(fileName, FileMode.Create, FileAccess.Write, FileShare.None);
                    await using Stream zipFileStream = entry.Open();
                    await zipFileStream.CopyToAsync(fileStream);

                    // Make program binaries executable
                    if (!string.IsNullOrEmpty(plugin.SbcExecutable) &&
                        (entry.FullName == "dsf/" + plugin.SbcExecutable || entry.FullName == $"dsf/{architecture}/{plugin.SbcExecutable}" ||
                            plugin.SbcExtraExecutables.Any(executable => (entry.FullName == "dsf/" + executable) || (entry.FullName == $"dsf/{architecture}/{executable}"))))
                    {
                        logger.LogDebug("Changing mode of {File} to 770", fileName);
                        File.SetUnixFileMode(fileName,
                            UnixFileMode.UserWrite | UnixFileMode.UserRead | UnixFileMode.UserExecute |
                            UnixFileMode.GroupWrite | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
                    }
                }
            }

            // Retrieve the SBC executable
            if (!string.IsNullOrEmpty(plugin.SbcExecutable))
            {
                string sbcExecutable = Path.Combine(pluginBase, "dsf", architecture, plugin.SbcExecutable);
                if (!File.Exists(sbcExecutable))
                {
                    sbcExecutable = Path.Combine(pluginBase, "dsf", plugin.SbcExecutable);
                }

                if (!File.Exists(sbcExecutable))
                {
                    throw new ArgumentException($"SBC executable {plugin.SbcExecutable} not found");
                }
            }

            // Install the web files. Try to use a symlink or copy the files if that failed
            foreach (string dwcFile in plugin.DwcFiles)
            {
                string pluginWwwPath = Path.Combine(pluginBase, "dwc", dwcFile);
                string installWwwPath = Path.Combine(sdPath, "www", dwcFile);

                // Create parent directory first
                string directory = Path.GetDirectoryName(installWwwPath)!;
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Remove any left-over file that may be blocking the path before creating a new one.
                // Path.Exists (unlike File.Exists) returns true for dead symlinks too
                if (Path.Exists(installWwwPath))
                {
                    File.Delete(installWwwPath);
                }

                // Attempt to symlink or copy the file
                try
                {
                    logger.LogDebug("Trying to create symlink {SourceFile} -> {File}", pluginWwwPath, installWwwPath);
                    File.CreateSymbolicLink(installWwwPath, pluginWwwPath);
                }
                catch (IOException e)
                {
                    logger.LogWarning(e, "Failed to create symlink to web directory, trying to copy web file instead...");
                    File.Copy(pluginWwwPath, installWwwPath, true);
                }
            }

            // Install refreshed plugin manifest
            logger.LogDebug("Installing plugin manifest");
            string manifestFilename = Path.Combine(settings.Value.PluginDirectory, $"{plugin.Id}.json");
            await using (FileStream manifestFile = new(manifestFilename, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(manifestFile, plugin, ObjectModelContext.Default.Plugin);
            }

            // Install Python packages. Because we're using a venv, this must happen after all other installation steps
            if (plugin.SbcPythonDependencies.Count > 0)
            {
                logger.LogDebug("Installing Python dependencies");
                await InstallPythonPackages(plugin.Id, cancellationToken);
            }
        }

        // Plugin installed
        using (await pluginStore.LockAsync(cancellationToken))
        {
            pluginStore.Plugins.Add(plugin);
        }

        // Done
        logger.LogInformation("Plugin installed");
    }

    /// <summary>
    /// Extract, parse, and verify the plugin manifest
    /// </summary>
    /// <param name="zipArchive">ZIP archive containing the plugin files</param>
    /// <returns>Plugin manifest</returns>
    /// <exception cref="ArgumentException">Plugin is incompatible</exception>
    private static async Task<Plugin> ExtractManifest(ZipArchive zipArchive)
    {
        // Extract the plugin manifest
        ZipArchiveEntry? manifestFile = zipArchive.GetEntry("plugin.json") ?? throw new ArgumentException("plugin.json not found in the ZIP file");
        Plugin plugin = new();
        await using (Stream manifestStream = manifestFile.Open())
        {
            using JsonDocument manifestJson = await JsonDocument.ParseAsync(manifestStream);
            plugin.UpdateFromJson(manifestJson.RootElement);
        }
        plugin.Pid = -1;

        // Check for reserved permissions
        if (plugin.SbcPermissions.HasFlag(SbcPermissions.ServicePlugins))
        {
            throw new ArgumentException("ServicePlugins permission is reserved for internal purposes");
        }

        // Plugins with Python dependencies are launched via bash -c with the arguments interpolated into the
        // double-quoted command string. Reject any char that could break out of that quoting and execute arbitrary code
        if (!string.IsNullOrEmpty(plugin.SbcExecutableArguments) &&
            plugin.SbcExecutableArguments.AsSpan().IndexOfAny(['"', '$', '`', '\\', '\n', '\r']) >= 0)
        {
            throw new ArgumentException("SbcExecutableArguments must not contain shell metacharacters (\", $, `, \\, newline)");
        }

        // All OK
        return plugin;
    }

    /// <summary>
    /// Global lock for installing system packages
    /// </summary>
    private static readonly AsyncLock _packageLock = new();

    /// <summary>
    /// Install a Linux package
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="package">Name of the package to install</param>
    private async Task InstallPackage(string package, CancellationToken cancellationToken)
    {
        if (!Environment.IsPrivilegedProcess)
        {
            throw new ArgumentException("Cannot install packages as regular user");
        }

        using (await _packageLock.LockAsync(cancellationToken))
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = _settings.InstallPackageCommand,
                Arguments = _settings.InstallPackageArguments.Replace("{package}", package)
            };
            foreach (var kv in _settings.InstallPackageEnvironment)
            {
                startInfo.EnvironmentVariables.Add(kv.Key, kv.Value);
            }

            using Process? process = Process.Start(startInfo);
            if (process is not null)
            {
                await process.WaitForExitAsync(cancellationToken);
                if (process.ExitCode != 0)
                {
                    throw new ArgumentException($"Failed to install package {package}, package manager exited with code {process.ExitCode}");
                }
            }
        }
    }

    /// <summary>
    /// Install a Python package
    /// </summary>
    /// <param name="plugin">Plugin identifier</param>
    private async Task InstallPythonPackages(string plugin, CancellationToken cancellationToken)
    {
        if (Environment.IsPrivilegedProcess)
        {
            throw new ArgumentException("Cannot install Python packages as root");
        }

        using (await _packageLock.LockAsync(cancellationToken))
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = _settings.InstallPythonPackageCommand,
                Arguments = _settings.InstallPythonPackageArguments
                    .Replace("{manifestFile}", Path.Combine(settings.Value.PluginDirectory, plugin + ".json"))
                    .Replace("{pluginPath}", Path.Combine(settings.Value.PluginDirectory, plugin))
            };
            foreach (var kv in _settings.InstallPackageEnvironment)
            {
                startInfo.EnvironmentVariables.Add(kv.Key, kv.Value);
            }

            using Process? process = Process.Start(startInfo);
            if (process is not null)
            {
                await process.WaitForExitAsync(cancellationToken);
                if (process.ExitCode != 0)
                {
                    throw new ArgumentException($"Failed to install packages for plugin {plugin}, package manager exited with code {process.ExitCode}");
                }
            }
        }
    }
}
