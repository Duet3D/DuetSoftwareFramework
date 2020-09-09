using DuetAPI.ObjectModel;
using System;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.UninstallPlugin"/> command
    /// </summary>
    public sealed class UninstallPlugin : DuetAPI.Commands.UninstallPlugin
    {
        /// <summary>
        /// Internal flag to be set when a plugin may be upgraded
        /// </summary>
        [JsonIgnore]
        public bool Upgrading { get; set; }

        /// <summary>
        /// Uninstall a plugin
        /// </summary>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="ArgumentException">Plugin is invalid</exception>
        public override async Task Execute()
        {
            NLog.Logger logger = NLog.LogManager.GetLogger($"UninstallPlugin#{Plugin}");

            // Stop the plugin first
            StopPlugin stopPlugin = new StopPlugin
            {
                Plugin = Plugin
            };
            await stopPlugin.Execute();

            // Remove the plugin from the object model
            Plugin plugin = null;
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                foreach (Plugin item in Model.Provider.Get.Plugins)
                {
                    if (item.Name == Plugin)
                    {
                        plugin = item;
                        Model.Provider.Get.Plugins.Remove(item);
                        break;
                    }
                }
            }

            if (plugin == null)
            {
                // should never get here...
                throw new ArgumentException("Invalid plugin");
            }

            // Remove the plugin manifest
            string manifestFile = Path.Combine(Settings.PluginDirectory, $"{Plugin}.json");
            if (File.Exists(manifestFile))
            {
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

            if (Upgrading)
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
    }
}
