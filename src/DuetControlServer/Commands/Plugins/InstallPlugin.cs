using DuetAPI.Connection;
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
    public class InstallPlugin : DuetAPI.Commands.InstallPlugin
    {
        /// <summary>
        /// Install or upgrade a plugin
        /// </summary>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="ArgumentException">Plugin is incompatible</exception>
        public override async Task Execute()
        {
            using ZipArchive zipArchive = ZipFile.OpenRead(PluginFile);
            Plugin plugin = await GetManifest(zipArchive);

            // Run preflight check to make sure no malicious files are installed
            foreach (ZipArchiveEntry entry in zipArchive.Entries)
            {
                if (entry.FullName.Contains("..") ||
                    entry.FullName == "sys/config.g" ||
                    entry.FullName == "sys/config-override.g" ||
                    entry.FullName.StartsWith("firmware/"))
                {
                    throw new ArgumentException($"Illegal filename {entry.FullName}, stopping installation");
                }
            }

            // Remove the old plugin files
            UninstallPlugin uninstallPlugin = new UninstallPlugin()
            {
                Plugin = plugin.Name,
                Upgrading = true
            };
            await uninstallPlugin.Execute();

            // Clear read-only fields
            plugin.RrfFiles.Clear();
            plugin.SbcFiles.Clear();
            plugin.PID = -1;
            // allow initial data to be specified

            // Install new plugin files
            bool hasWebDirectory = false;
            foreach (ZipArchiveEntry entry in zipArchive.Entries)
            {
                // Ignore manifest.json, it will be written when this archive has been extracted
                if (entry.FullName == "manifest.json")
                {
                    continue;
                }

                string fileName;
                if (entry.FullName.StartsWith("rrf/"))
                {
                    // Put RRF files into 0:/
                    fileName = Path.Combine(Settings.BaseDirectory, entry.FullName);
                    plugin.RrfFiles.Add(entry.FullName.Substring(4));
                }
                else
                {
                    // Put other files into <PluginDirectory>/<PluginName>/
                    fileName = Path.Combine(Settings.PluginDirectory, plugin.Name, entry.FullName);
                    plugin.SbcFiles.Add(entry.FullName);
                }

                // Check if there are any web files
                if (!hasWebDirectory && entry.FullName.StartsWith("www/"))
                {
                    hasWebDirectory = true;
                }

                // Extract the file
                using FileStream fileStream = new FileStream(fileName, FileMode.Create, FileAccess.Write, FileShare.None);
                using Stream zipFileStream = entry.Open();
                await zipFileStream.CopyToAsync(fileStream);
            }

            // Create a symlink to the www directory if it is present
            if (hasWebDirectory)
            {
                string symlinkPath = Path.Combine(Settings.BaseDirectory, "www", plugin.Name);
                if (!File.Exists(symlinkPath))
                {
                    string wwwPath = Path.Combine(Settings.PluginDirectory, plugin.Name, "www");
                    LinuxDevices.Symlink.Create(wwwPath, symlinkPath);
                }
            }

            // Install refreshed plugin manifest
            string manifestFilename = Path.Combine(Settings.PluginDirectory, $"{plugin.Name}.json");
            using FileStream manifestFile = new FileStream(manifestFilename, FileMode.Create, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(manifestFile, plugin, JsonHelper.DefaultJsonOptions);

            // Add it to the object model
            using (await Model.Provider.AccessReadWriteAsync())
            {
                Model.Provider.Get.Plugins.Add(plugin);
            }
        }

        /// <summary>
        /// Extract, parse, and verify the plugin manifest
        /// </summary>
        /// <param name="zipArchive"></param>
        /// <returns>Plugin manifest</returns>
        /// <exception cref="ArgumentException">Plugin is incompatible</exception>
        private async Task<Plugin> GetManifest(ZipArchive zipArchive)
        {
            // Extract the file
            ZipArchiveEntry manifestFile = zipArchive.GetEntry("manifest.json");
            if (manifestFile == null)
            {
                throw new ArgumentException("manifest.json not found in the ZIP file");
            }

            using Stream manifestStream = manifestFile.Open();
            Plugin plugin = await JsonSerializer.DeserializeAsync<Plugin>(manifestStream, JsonHelper.DefaultJsonOptions);

            // Does it contain SBC files?
            if (!string.IsNullOrEmpty(plugin.SbcExecutable))
            {
                // Check the API version
                if (plugin.SbcApiVersion < IPC.Server.MinimumProtocolVersion || plugin.SbcApiVersion > Defaults.ProtocolVersion)
                {
                    throw new ArgumentException("Incompatible API version");
                }
            }

#warning Optional RRF version isn't checked here

            // All OK
            return plugin;
        }
    }
}
