using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DuetPluginService.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.UninstallPlugin"/> command
    /// </summary>
    public sealed class UninstallPlugin : DuetAPI.Commands.UninstallPlugin
    {
        /// <summary>
        /// Internal flag to indicate that custom plugin files should not be purged
        /// </summary>
        public bool ForUpgrade { get; set; }

        /// <summary>
        /// Uninstall a plugin
        /// </summary>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="ArgumentException">Plugin is invalid</exception>
        public override async Task Execute()
        {
            NLog.Logger logger = NLog.LogManager.GetLogger(Plugin);

            using (await Plugins.LockAsync())
            {
                // Get the plugin first
                Plugin? plugin = null;
                foreach (Plugin item in Plugins.List)
                {
                    if (item.Id == Plugin)
                    {
                        plugin = item;
                        break;
                    }
                }

                if (plugin is null)
                {
                    throw new ArgumentException($"Plugin {Plugin} not found by {(Program.IsRoot ? "root service" : "service")}");
                }
                if (plugin.Pid > 0)
                {
                    throw new ArgumentException("Plugin must be stopped before it can be uninstalled");
                }

                // Root plugins are deleted by the root service to avoid potential permission issues
                if (plugin.SbcPermissions.HasFlag(SbcPermissions.SuperUser) == Program.IsRoot)
                {
                    string manifestFile = Path.Combine(Settings.PluginDirectory, $"{Plugin}.json");

                    // Check if the manifest is writable
                    LinuxApi.Commands.GetPermissions(manifestFile, out LinuxApi.UnixPermissions userPermission, out _, out _);
                    if (!userPermission.HasFlag(LinuxApi.UnixPermissions.Write))
                    {
                        throw new ArgumentException("Plugin cannot be uninstalled via API");
                    }

                    // Remove the plugin manifest
                    logger.Info("Uninstalling plugin {0}", Plugin + (ForUpgrade ? " for upgrade" : string.Empty));
                    if (File.Exists(manifestFile))
                    {
                        logger.Debug("Removing plugin manifest");
                        File.Delete(manifestFile);
                    }

                    // Remove installed files and directories from the dwc and www directories
                    foreach (string dwcFile in plugin.DwcFiles)
                    {
                        string installWwwPath = Path.Combine(Settings.BaseDirectory, "www", dwcFile);
                        if (File.Exists(installWwwPath))
                        {
                            logger.Debug("Removing {0}", installWwwPath);
                            File.Delete(installWwwPath);
                        }

                        string directory = Path.GetDirectoryName(installWwwPath)!;
                        if (!Directory.EnumerateFileSystemEntries(directory).Any())
                        {
                            logger.Debug("Removing {0}", directory);
                            Directory.Delete(directory);
                        }
                    }

                    if (ForUpgrade)
                    {
                        // Remove only installed files
                        foreach (string dsfFile in plugin.DsfFiles)
                        {
                            string file = Path.Combine(Settings.PluginDirectory, Plugin, "dsf", dsfFile);
                            if (File.Exists(file))
                            {
                                logger.Debug("Deleting file {0}", file);
                                File.Delete(file);
                            }
                        }

                        foreach (string dwcFile in plugin.DwcFiles)
                        {
                            string file = Path.Combine(Settings.PluginDirectory, Plugin, "dwc", dwcFile);
                            if (File.Exists(file))
                            {
                                logger.Debug("Deleting file {0}", file);
                                File.Delete(file);
                            }
                        }

                        foreach (string sdFile in plugin.SdFiles)
                        {
                            string fileName = Path.Combine(Settings.BaseDirectory, sdFile);
                            if (File.Exists(fileName) && !plugin.SbcConfigFiles.Any(file => fileName == Path.Combine(Settings.BaseDirectory, "sys", file) || fileName == Path.Combine(Settings.BaseDirectory, file)))
                            {
                                if (Path.GetFileName(sdFile).Equals("daemon.g"))
                                {
                                    // daemon.g may be still open at this time
                                    logger.Debug("Renaming file {0} to {1}", sdFile, sdFile + ".bak");
                                    File.Move(sdFile, sdFile + ".bak", true);
                                }
                                else
                                {
                                    logger.Debug("Deleting file {0}", fileName);
                                    File.Delete(fileName);
                                }
                            }
                        }
                    }
                    else
                    {
                        // Remove the full plugin directory
                        string pluginDirectory = Path.Combine(Settings.PluginDirectory, Plugin);
                        if (Directory.Exists(pluginDirectory))
                        {
                            logger.Debug("Removing plugin directory {0}", pluginDirectory);
                            Directory.Delete(pluginDirectory, true);
                        }
                    }
                }

                // Remove the security policy
                if (Program.IsRoot)
                {
                    await Permissions.Manager.UninstallProfile(plugin);
                }

                // Plugin has been uninstalled
                Plugins.List.Remove(plugin);
                logger.Info("Plugin {0} has been uninstalled", Plugin);
            }
        }
    }
}
