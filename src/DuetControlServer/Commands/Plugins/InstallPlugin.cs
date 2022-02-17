using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.InstallPlugin"/> command
    /// </summary>
    public sealed class InstallPlugin : DuetAPI.Commands.InstallPlugin
    {
        /// <summary>
        /// Internal flag to indicate that custom plugin files should not be purged
        /// </summary>
        public bool Upgrade { get; set; }

        /// <summary>
        /// Install or upgrade a plugin
        /// </summary>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="ArgumentException">Plugin is incompatible</exception>
        public override async Task Execute()
        {
            if (!Settings.PluginSupport)
            {
                throw new NotSupportedException("Plugin support has been disabled");
            }

            Plugin plugin;
            using (ZipArchive zipArchive = ZipFile.OpenRead(PluginFile))
            {
                // Get the plugin manifest from the ZIP file
                plugin = await ExtractManifest(zipArchive);

                // Run preflight check to make sure no malicious files are installed
                foreach (ZipArchiveEntry entry in zipArchive.Entries)
                {
                    if (entry.FullName.Contains("..") ||
                        entry.FullName == "sd/sys/config.g" ||
                        entry.FullName == "sd/sys/config-override.g" ||
                        entry.FullName.StartsWith("sd/firmware/"))
                    {
                        throw new ArgumentException($"Illegal filename {entry.FullName}, stopping installation");
                    }
                }
            }

            // Permit root plugins only if they're enabled
            if (plugin.SbcPermissions.HasFlag(SbcPermissions.SuperUser) && !Settings.RootPluginSupport)
            {
                throw new ArgumentException("Installation of plugins with super-user permissions is not allowed");
            }

            // Validate the current DSF/RRF versions
            using (await Model.Provider.AccessReadOnlyAsync())
            {
#pragma warning disable CS0618 // Type or member is obsolete
                // Check the required DSF version
                if (!PluginManifest.CheckVersion(Model.Provider.Get.State.DsfVersion, plugin.SbcDsfVersion))
                {
                    throw new ArgumentException($"Incompatible DSF version (requires {plugin.SbcDsfVersion}, got {Model.Provider.Get.State.DsfVersion})");
                }
#pragma warning restore CS0618 // Type or member is obsolete

                // Check the required RRF version
                if (!string.IsNullOrEmpty(plugin.RrfVersion))
                {
                    if (Model.Provider.Get.Boards.Count > 0)
                    {
                        if (!PluginManifest.CheckVersion(Model.Provider.Get.Boards[0].FirmwareVersion, plugin.RrfVersion))
                        {
                            throw new ArgumentException($"Incompatible RRF version (requires {plugin.RrfVersion}, got {Model.Provider.Get.Boards[0].FirmwareVersion})");
                        }
                    }
                    else
                    {
                        throw new ArgumentException("Failed to check RRF version");
                    }
                }
            }

            // Make sure all the required plugins dependencies are installed
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                foreach (string dependency in plugin.SbcPluginDependencies)
                {
                    if (!Model.Provider.Get.Plugins.ContainsKey(dependency))
                    {
                        throw new ArgumentException($"Missing plugin dependency {dependency}");
                    }
                }
            }

            // Validate package dependencies to prevent potentially dangerous command injection
            foreach (string package in plugin.SbcPackageDependencies)
            {
                foreach (char c in package)
                {
                    if (!char.IsLetterOrDigit(c) && c != '.' && c != '-' && c != '_' && c != '+')
                    {
                        throw new ArgumentException($"Illegal characters in required package {package}");
                    }
                }
            }

            foreach (string package in plugin.SbcPythonDependencies)
            {
                foreach (char c in package)
                {
                    if (!char.IsLetterOrDigit(c) && c != '.' && c != '-' && c != '_' && c != '+' && c != '<' && c != '>' && c != '=' && c != ',')
                    {
                        throw new ArgumentException($"Illegal characters in required Python package {package}");
                    }
                }
            }

            // Uninstall the old plugin (if applicable)
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                Upgrade = Model.Provider.Get.Plugins.ContainsKey(plugin.Id);
            }

            if (Upgrade)
            {
                UninstallPlugin uninstallCommand = new() { Plugin = plugin.Id, ForUpgrade = true };
                await uninstallCommand.Execute();
            }

            // Forward this command to the plugin services
            // 1) Install regular files via dsf user
            // 2) Perform policy generation using AppArmor profiles via root
            await IPC.Processors.PluginService.PerformCommand(this, false);
            await IPC.Processors.PluginService.PerformCommand(this, true);

            // If possible, reload the plugin manifest with the updated file lists and register it in the object model
            string manifestFilename = Path.Combine(Settings.PluginDirectory, plugin.Id + ".json");
            if (File.Exists(manifestFilename))
            {
                using (FileStream manifestStream = new(manifestFilename, FileMode.Open, FileAccess.Read))
                {
                    using JsonDocument manifestJson = await JsonDocument.ParseAsync(manifestStream);
                    plugin.UpdateFromJson(manifestJson.RootElement, false);
                }

                using (await Model.Provider.AccessReadWriteAsync())
                {
                    Model.Provider.Get.Plugins.Add(plugin.Id, plugin);
                }
            }
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
            ZipArchiveEntry manifestFile = zipArchive.GetEntry("plugin.json");
            if (manifestFile == null)
            {
                throw new ArgumentException("plugin.json not found in the ZIP file");
            }

            Plugin plugin = new();
            using (Stream manifestStream = manifestFile.Open())
            {
                using JsonDocument manifestJson = await JsonDocument.ParseAsync(manifestStream);
                plugin.UpdateFromJson(manifestJson.RootElement, false);
            }
            plugin.Pid = -1;

            // Check for reserved permissions
            if (plugin.SbcPermissions.HasFlag(SbcPermissions.ServicePlugins))
            {
                throw new ArgumentException("ServicePlugins permission is reserved for internal purposes");
            }

            // All OK
            return plugin;
        }
    }
}
