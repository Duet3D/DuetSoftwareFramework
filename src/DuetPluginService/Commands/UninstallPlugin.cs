using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetPluginService.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DuetPluginService.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.UninstallPlugin"/> command
/// </summary>
public sealed class UninstallPlugin(PluginManager pluginManager, ILoggerFactory loggerFactory, IOptions<Settings> settings) : DuetAPI.Commands.UninstallPlugin
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
    public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        ILogger logger = loggerFactory.CreateLogger($"Plugin {Plugin}");

        using (await pluginManager.LockAsync(cancellationToken))
        {
            // Get the plugin first
            Plugin? plugin = null;
            foreach (Plugin item in pluginManager.Plugins)
            {
                if (item.Id == Plugin)
                {
                    plugin = item;
                    break;
                }
            }

            if (plugin is null)
            {
                throw new ArgumentException($"Plugin {Plugin} not found by {(Utility.IsRoot ? "root service" : "service")}");
            }
            if (plugin.Pid > 0)
            {
                throw new ArgumentException("Plugin must be stopped before it can be uninstalled");
            }

            // Root plugins are deleted by the root service to avoid potential permission issues
            if (plugin.SbcPermissions.HasFlag(SbcPermissions.SuperUser) == Utility.IsRoot)
            {
                string manifestFile = Path.Combine(_settings.PluginDirectory, $"{Plugin}.json");

                // Check if the manifest is writable
                LinuxApi.Commands.GetPermissions(manifestFile, out LinuxApi.UnixPermissions userPermission, out _, out _);
                if (!userPermission.HasFlag(LinuxApi.UnixPermissions.Write))
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
                    string installWwwPath = Path.Combine(_settings.BaseDirectory, "www", dwcFile);
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
                        string file = Path.Combine(_settings.PluginDirectory, Plugin, "dsf", dsfFile);
                        if (File.Exists(file))
                        {
                            logger.LogDebug("Deleting file {File}", file);
                            File.Delete(file);
                        }
                    }

                    foreach (string dwcFile in plugin.DwcFiles)
                    {
                        string file = Path.Combine(_settings.PluginDirectory, Plugin, "dwc", dwcFile);
                        if (File.Exists(file))
                        {
                            logger.LogDebug("Deleting file {File}", file);
                            File.Delete(file);
                        }
                    }

                    foreach (string sdFile in plugin.SdFiles)
                    {
                        string fileName = Path.Combine(_settings.BaseDirectory, sdFile);
                        if (File.Exists(fileName) && !plugin.SbcConfigFiles.Any(file => fileName == Path.Combine(_settings.BaseDirectory, "sys", file) || fileName == Path.Combine(_settings.BaseDirectory, file)))
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
                    string pluginDirectory = Path.Combine(_settings.PluginDirectory, Plugin);
                    if (Directory.Exists(pluginDirectory))
                    {
                        logger.LogDebug("Removing plugin directory {Directory}", pluginDirectory);
                        Directory.Delete(pluginDirectory, true);
                    }
                }
            }

            // Remove the security policy
            if (Utility.IsRoot && !_settings.DisableAppArmor)
            {
                await Permissions.AppArmor.UninstallProfileAsync(Plugin, _settings, cancellationToken);
            }

            // Plugin has been uninstalled
            pluginManager.Plugins.Remove(plugin);
            logger.LogInformation("Plugin has been uninstalled");
        }
    }
}