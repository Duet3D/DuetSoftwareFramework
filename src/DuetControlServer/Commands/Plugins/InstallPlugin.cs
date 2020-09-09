using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
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
        /// Logger instance
        /// </summary>
        private NLog.Logger _logger;

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
            _logger = NLog.LogManager.GetLogger($"InstallPlugin#{plugin.Name}");
            _logger.Debug("Checking files");
            foreach (ZipArchiveEntry entry in zipArchive.Entries)
            {
                if (entry.FullName.Contains("..") ||
                    entry.FullName == "rrf/sys/config.g" ||
                    entry.FullName == "rrf/sys/config-override.g" ||
                    entry.FullName.StartsWith("rrf/firmware/"))
                {
                    throw new ArgumentException($"Illegal filename {entry.FullName}, stopping installation");
                }
            }

            // Remove the old plugin files
            bool pluginFound = false;
            using (await Model.Provider.AccessReadWriteAsync())
            {
                foreach (Plugin item in Model.Provider.Get.Plugins)
                {
                    if (item.Name == plugin.Name)
                    {
                        pluginFound = true;
                        break;
                    }
                }

            }

            if (pluginFound)
            {
                _logger.Debug("Uninstalling old files for upgrade");
                UninstallPlugin uninstallPlugin = new UninstallPlugin()
                {
                    Plugin = plugin.Name,
                    Upgrading = true
                };
                await uninstallPlugin.Execute();
            }

            // Clear file lists, they are assigned during the installation
            plugin.DwcFiles.Clear();
            plugin.RrfFiles.Clear();
            plugin.SbcFiles.Clear();

            // Install plugin dependencies
            foreach (string package in plugin.SbcPackageDependencies)
            {
                _logger.Debug("Installing dependency package {0}", package);
                await InstallPackage(package);
            }

            // Make plugin directory
            string pluginBase = Path.Combine(Settings.PluginDirectory, plugin.Name);
            if (!Directory.Exists(pluginBase))
            {
                _logger.Debug("Creating plugin base directory {0}", pluginBase);
                Directory.CreateDirectory(pluginBase);
            }

            // Install new plugin files
            foreach (ZipArchiveEntry entry in zipArchive.Entries)
            {
                // Ignore plugin.json, it will be written when this archive has been extracted
                // Also ignore directories, they are automatically created below
                if (entry.FullName == "plugin.json" || entry.FullName.EndsWith('/'))
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
                    fileName = Path.Combine(pluginBase, entry.FullName);

                    // Check what type of file this is
                    if (entry.FullName.StartsWith("www/"))
                    {
                        plugin.DwcFiles.Add(entry.FullName.Substring(4));
                    }
                    else
                    {
                        plugin.SbcFiles.Add(entry.FullName);
                    }
                }

                // Make sure the parent directory exists
                string parentDirectory = Path.GetDirectoryName(fileName);
                if (!Directory.Exists(parentDirectory))
                {
                    _logger.Debug("Creating new directory {0}", parentDirectory);
                    Directory.CreateDirectory(parentDirectory);
                }

                // Extract the file
                _logger.Debug("Extracting {0} to {1}", entry.FullName, fileName);
                using FileStream fileStream = new FileStream(fileName, FileMode.Create, FileAccess.Write, FileShare.None);
                using Stream zipFileStream = entry.Open();
                await zipFileStream.CopyToAsync(fileStream);
            }

            // Make the SBC executable
            if (!string.IsNullOrEmpty(plugin.SbcExecutable))
            {
                string architecture = RuntimeInformation.OSArchitecture switch
                {
                    Architecture.Arm => "arm",
                    Architecture.Arm64 => "arm64",
                    Architecture.X86 => "x86",
                    Architecture.X64 => "x86_64",
                    _ => "unknown"
                };

                string sbcExecutable = Path.Combine(pluginBase, "bin", architecture, plugin.SbcExecutable);
                if (!File.Exists(sbcExecutable))
                {
                    sbcExecutable = Path.Combine(pluginBase, "bin", plugin.SbcExecutable);
                }

                if (File.Exists(sbcExecutable))
                {
                    _logger.Debug("Changing mode of {0} to 750", sbcExecutable);
                    LinuxApi.Commands.Chmod(sbcExecutable,
                        LinuxApi.UnixPermissions.Read | LinuxApi.UnixPermissions.Write | LinuxApi.UnixPermissions.Execute,
                        LinuxApi.UnixPermissions.Read | LinuxApi.UnixPermissions.Execute,
                        LinuxApi.UnixPermissions.None);
                }
                else
                {
                    throw new ArgumentException("SBC executable {0} not found", plugin.SbcExecutable);
                }
            }

            // Install the web files. Try to use a symlink or copy the files if that failed
            if (plugin.DwcFiles.Count > 0)
            {
                string pluginWwwPath = Path.Combine(pluginBase, "www");
                string installWwwPath = Path.Combine(Settings.BaseDirectory, "www", plugin.Name);

                bool createDirectory = false;
                if (!File.Exists(installWwwPath))
                {
                    try
                    {
                        _logger.Debug("Trying to create symlink {0} -> {1}", pluginWwwPath, installWwwPath);
                        LinuxApi.Commands.Symlink(pluginWwwPath, installWwwPath);
                    }
                    catch (IOException e)
                    {
                        _logger.Debug(e);
                        _logger.Warn("Failed to create symlink to web directory, trying to copy web files instead...");
                        createDirectory = true;
                    }
                }

                if (createDirectory)
                {
                    _logger.Debug("Copying web files from {0} to {1}", pluginWwwPath, installWwwPath);
                    DirectoryCopy(pluginWwwPath, installWwwPath, true);
                }
            }

            // Install refreshed plugin manifest
            string manifestFilename = Path.Combine(Settings.PluginDirectory, $"{plugin.Name}.json");
            _logger.Debug("Installing plugin manifest {0}", manifestFilename);
            using FileStream manifestFile = new FileStream(manifestFilename, FileMode.Create, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(manifestFile, plugin, JsonHelper.DefaultJsonOptions);

            // Add it to the object model
            using (await Model.Provider.AccessReadWriteAsync())
            {
                Model.Provider.Get.Plugins.Add(plugin);
            }
            _logger.Info("Plugin successfully installed");
        }

        /// <summary>
        /// Install a Linux package
        /// </summary>
        /// <param name="package">Name of the package to install</param>
        private Task InstallPackage(string package)
        {
            // TODO Ask elevation service to install this package
            return Task.CompletedTask;
        }

        /// <summary>
        /// Extract, parse, and verify the plugin manifest
        /// </summary>
        /// <param name="zipArchive"></param>
        /// <returns>Plugin manifest</returns>
        /// <exception cref="ArgumentException">Plugin is incompatible</exception>
        private async Task<Plugin> GetManifest(ZipArchive zipArchive)
        {
            // Extract the plugin manifest
            ZipArchiveEntry manifestFile = zipArchive.GetEntry("plugin.json");
            if (manifestFile == null)
            {
                throw new ArgumentException("plugin.json not found in the ZIP file");
            }

            Plugin plugin = new Plugin();
            using (Stream manifestStream = manifestFile.Open())
            {
                using JsonDocument manifestJson = await JsonDocument.ParseAsync(manifestStream);
                plugin.UpdateFromJson(manifestJson.RootElement);
            }
            plugin.Pid = -1;

            // Check the plugin name
            if (string.IsNullOrWhiteSpace(plugin.Name) || plugin.Name.Length > 64)
            {
                throw new ArgumentException("Invalid plugin name");
            }

            foreach (char c in plugin.Name)
            {
                if (!char.IsLetterOrDigit(c) && c != ' ' && c != '.' && c != '-' && c != '_')
                {
                    throw new ArgumentException("Illegal plugin name");
                }
            }

            // All OK
            return plugin;
        }

        /// <summary>
        /// Copy an entire directory
        /// </summary>
        /// <param name="sourceDirName">Source directory</param>
        /// <param name="destDirName">Destination directory</param>
        /// <param name="copySubDirs">True if sub directories may be copied</param>
        /// <remarks>
        /// Copied from https://docs.microsoft.com/de-de/dotnet/standard/io/how-to-copy-directories
        /// </remarks>
        private static void DirectoryCopy(string sourceDirName, string destDirName, bool copySubDirs)
        {
            // Get the subdirectories for the specified directory.
            DirectoryInfo dir = new DirectoryInfo(sourceDirName);

            if (!dir.Exists)
            {
                throw new DirectoryNotFoundException(
                    "Source directory does not exist or could not be found: "
                    + sourceDirName);
            }

            DirectoryInfo[] dirs = dir.GetDirectories();
            // If the destination directory doesn't exist, create it.
            if (!Directory.Exists(destDirName))
            {
                Directory.CreateDirectory(destDirName);
            }

            // Get the files in the directory and copy them to the new location.
            FileInfo[] files = dir.GetFiles();
            foreach (FileInfo file in files)
            {
                string temppath = Path.Combine(destDirName, file.Name);
                file.CopyTo(temppath, false);
            }

            // If copying subdirectories, copy them and their contents to new location.
            if (copySubDirs)
            {
                foreach (DirectoryInfo subdir in dirs)
                {
                    string temppath = Path.Combine(destDirName, subdir.Name);
                    DirectoryCopy(subdir.FullName, temppath, copySubDirs);
                }
            }
        }
    }
}
