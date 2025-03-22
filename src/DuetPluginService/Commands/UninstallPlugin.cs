using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetAPIClient;
using DuetPluginService.PermissionManagers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace DuetPluginService.IPC;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.UninstallPlugin"/> command
/// </summary>
/// <param name="permissionManager">Permission manager</param>
/// <param name="pluginStore">Plugin store</param>
/// <param name="hostEnvironment">Host environment</param>
/// <param name="loggerFactory">Logger factory</param>
/// <param name="settings">Application settings</param>
public sealed class UninstallPlugin(IPermissionManager permissionManager, PluginStore pluginStore, IHostEnvironment hostEnvironment, ILoggerFactory loggerFactory, IOptions<Settings> settings) : DuetAPI.Commands.UninstallPlugin
{
    private readonly Settings _settings = settings.Value;

    /// <summary>
    /// Internal flag to indicate that custom plugin files should not be purged
    /// </summary>
    public bool ForUpgrade { get; set; }

    /// <summary>
    /// Uninstall a plugin
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="ArgumentException">Plugin is invalid</exception>
    [UnsupportedOSPlatform("windows")]
    public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        ILogger logger = loggerFactory.CreateLogger($"Plugin {Plugin}");

        // Obtain virtual SD path
        string sdPath;
        using (CommandConnection connection = new())
        {
            await connection.ConnectAsync(_settings.SocketPath, cancellationToken);
            sdPath = await connection.ResolvePathAsync("0:/", cancellationToken);
        }

        // Get the plugin first
        Plugin? plugin = null;
        foreach (Plugin item in pluginStore.Plugins)
        {
            if (item.Id == Plugin)
            {
                plugin = item;
                break;
            }
        }

        if (plugin is null)
        {
            throw new ArgumentException($"Plugin {Plugin} not found by {(Environment.IsPrivilegedProcess ? "root service" : "service")}");
        }
        if (plugin.Pid > 0)
        {
            throw new ArgumentException("Plugin must be stopped before it can be uninstalled");
        }

        // Root plugins are deleted by the root service to avoid potential permission issues
        if (plugin.SbcPermissions.HasFlag(SbcPermissions.SuperUser) == Environment.IsPrivilegedProcess)
        {
            string manifestFile = Path.Combine(hostEnvironment.ContentRootPath, $"{Plugin}.json");

            // Check if the manifest is writable
            UnixFileMode fileMode = File.GetUnixFileMode(manifestFile);
            if (!fileMode.HasFlag(UnixFileMode.UserWrite))
            {
                throw new ArgumentException("Plugin cannot be uninstalled via API");
            }

            // Remove the plugin manifest
            if (ForUpgrade)
            {
                logger.LogInformation("Uninstalling plugin {Plugin} for upgrade", Plugin);
            }
            else
            {
                logger.LogInformation("Uninstalling plugin {Plugin}", Plugin);
            }

            if (File.Exists(manifestFile))
            {
                logger.LogDebug("Removing plugin manifest");
                File.Delete(manifestFile);
            }

            // Remove installed files and directories from the dwc and www directories
            foreach (string dwcFile in plugin.DwcFiles)
            {
                string installWwwPath = Path.Combine(sdPath, "www", dwcFile);
                if (File.Exists(installWwwPath))
                {
                    logger.LogDebug("Removing {File}", installWwwPath);
                    File.Delete(installWwwPath);
                }

                string directory = Path.GetDirectoryName(installWwwPath)!;
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    logger.LogDebug("Removing {Directory}", directory);
                    Directory.Delete(directory);
                }
            }

            if (ForUpgrade)
            {
                // Remove only installed files
                foreach (string dsfFile in plugin.DsfFiles)
                {
                    string file = Path.Combine(hostEnvironment.ContentRootPath, Plugin, "dsf", dsfFile);
                    if (File.Exists(file))
                    {
                        logger.LogDebug("Deleting file {File}", file);
                        File.Delete(file);
                    }
                }

                foreach (string dwcFile in plugin.DwcFiles)
                {
                    string file = Path.Combine(hostEnvironment.ContentRootPath, Plugin, "dwc", dwcFile);
                    if (File.Exists(file))
                    {
                        logger.LogDebug("Deleting file {File}", file);
                        File.Delete(file);
                    }
                }

                foreach (string sdFile in plugin.SdFiles)
                {
                    string fileName = Path.Combine(sdPath, sdFile);
                    if (File.Exists(fileName) && !plugin.SbcConfigFiles.Any(file => fileName == Path.Combine(sdPath, "sys", file) || fileName == Path.Combine(sdPath, file)))
                    {
                        if (Path.GetFileName(sdFile).Equals("daemon.g"))
                        {
                            // daemon.g may be still open at this time
                            logger.LogDebug("Renaming file {SourceFile} to {File}", sdFile, sdFile + ".bak");
                            File.Move(sdFile, sdFile + ".bak", true);
                        }
                        else
                        {
                            logger.LogDebug("Deleting file {File}", fileName);
                            File.Delete(fileName);
                        }
                    }
                }
            }
            else
            {
                // Remove the full plugin directory
                string pluginDirectory = Path.Combine(hostEnvironment.ContentRootPath, Plugin);
                if (Directory.Exists(pluginDirectory))
                {
                    logger.LogDebug("Removing plugin directory {Directory}", pluginDirectory);
                    Directory.Delete(pluginDirectory, true);
                }
            }
        }

        // Remove the security policy
        if (Environment.IsPrivilegedProcess && !_settings.DisableAppArmor)
        {
            await permissionManager.UninstallProfileAsync(plugin, cancellationToken);
        }

        // Plugin has been uninstalled
        using (await pluginStore.LockAsync(cancellationToken))
        {
            pluginStore.Plugins.Remove(plugin);
        }
        logger.LogInformation("Plugin uninstalled");
    }
}
