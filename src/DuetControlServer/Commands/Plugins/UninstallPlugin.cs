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
    public class UninstallPlugin : DuetAPI.Commands.UninstallPlugin
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
        /// <exception cref="ArgumentException">Plugin is incompatible</exception>
        public override async Task Execute()
        {
            // Stop the plugin first
            StopPlugin stopPlugin = new StopPlugin
            {
                Plugin = Plugin
            };
            await stopPlugin.Execute();

            // Uninstall it
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                foreach (Plugin item in Model.Provider.Get.Plugins)
                {
                    if (item.Name == Plugin)
                    {
                        // Remove the plugin from the object model
                        Model.Provider.Get.Plugins.Remove(item);

                        // Remove symlink from www if present
                        string symlinkPath = Path.Combine(Settings.BaseDirectory, "www", Plugin);
                        if (File.Exists(symlinkPath))
                        {
                            File.Delete(symlinkPath);
                        }

                        if (Upgrading)
                        {
                            // Remove only installed files
                            foreach (string rrfFile in item.RrfFiles)
                            {
                                string file = Path.Combine(Settings.BaseDirectory, rrfFile);
                                if (File.Exists(file))
                                {
                                    File.Delete(file);
                                }
                            }

                            foreach (string sbcFile in item.SbcFiles)
                            {
                                string file = Path.Combine(Settings.PluginDirectory, Plugin, sbcFile);
                                if (File.Exists(file))
                                {
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
                                Directory.Delete(pluginDirectory, true);
                            }
                        }
                        break;
                    }
                }
            }
            throw new ArgumentException("Invalid plugin");
        }
    }
}
