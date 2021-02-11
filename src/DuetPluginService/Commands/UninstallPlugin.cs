﻿using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using System;
using System.IO;
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
                Plugin plugin = null;
                foreach (Plugin item in Plugins.List)
                {
                    if (item.Name == Plugin)
                    {
                        plugin = item;
                        break;
                    }
                }
                if (plugin == null)
                {
                    throw new ArgumentException($"Plugin {Plugin} not found by {(Program.IsRoot ? "root service" : "service")}");
                }
                if (plugin.Pid > 0)
                {
                    throw new ArgumentException("Plugin must be stopped before it can be uninstalled");
                }

                // Uninstall the plugin instance
                logger.Info("Uninstalling plugin {0}", Plugin + (ForUpgrade ? " for upgrade" : string.Empty));

                // Root plugins are deleted by the root service to avoid potential permission issues
                if (plugin.SbcPermissions.HasFlag(SbcPermissions.SuperUser) == Program.IsRoot)
                {
                    // Remove the plugin manifest
                    string manifestFile = Path.Combine(Settings.PluginDirectory, $"{Plugin}.json");
                    if (File.Exists(manifestFile))
                    {
                        logger.Debug("Removing plugin manifest");
                        File.Delete(manifestFile);
                    }

                    // Remove symlink from www if present
                    string installWwwPath = Path.Combine(Settings.BaseDirectory, "www", Plugin);
                    if (Directory.Exists(installWwwPath))
                    {
                        logger.Debug("Removing installed www directory");
                        Directory.Delete(installWwwPath, true);
                    }
                    else if (File.Exists(installWwwPath))
                    {
                        logger.Debug("Removing www symlink");
                        File.Delete(installWwwPath);
                    }

                    if (ForUpgrade)
                    {
                        // Remove only installed files
                        foreach (string rrfFile in plugin.RrfFiles)
                        {
                            string file = Path.Combine(Settings.BaseDirectory, rrfFile);
                            if (File.Exists(file))
                            {
                                logger.Debug("Deleting file {0}", file);
                                File.Delete(file);
                            }
                        }

                        foreach (string sbcFile in plugin.SbcFiles)
                        {
                            string file = Path.Combine(Settings.PluginDirectory, Plugin, sbcFile);
                            if (File.Exists(file))
                            {
                                logger.Debug("Deleting file {0}", file);
                                File.Delete(file);
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