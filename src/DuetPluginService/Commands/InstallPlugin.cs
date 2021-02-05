using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using Nito.AsyncEx;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace DuetPluginService.Commands
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
        /// Internal flag to indicate that custom plugin files should not be purged
        /// </summary>
        public bool Upgrade { get; set; }

        /// <summary>
        /// Install or upgrade a plugin
        /// </summary>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="ArgumentException">Plugin installation failed</exception>
        public override async Task Execute()
        {
            // Extract the plugin manifest
            using ZipArchive zipArchive = ZipFile.OpenRead(PluginFile);
            Plugin plugin = await ExtractManifest(zipArchive);
            _logger = NLog.LogManager.GetLogger($"Plugin {plugin.Name}");

            // Install plugin dependencies as root
            if (Program.IsRoot)
            {
                foreach (string package in plugin.SbcPackageDependencies)
                {
                    _logger.Info("Installing package {0}", package);
                    await InstallPackage(package);
                }
            }

            if (Program.IsRoot)
            {
                // Apply security profile for this plugin
                await Permissions.Manager.InstallProfile(plugin);
                _logger.Info("Security profile installed");
            }
            else
            {
                // Delete old files
                string pluginBase = Path.Combine(Settings.PluginDirectory, plugin.Name);
                if (!Upgrade && Directory.Exists(pluginBase))
                {
                    try
                    {
                        _logger.Warn("Deleting previous installation directory");
                        Directory.Delete(pluginBase, true);
                    }
                    catch
                    {
                        _logger.Error("Failed to remove previous installation directory {0}", pluginBase);
                        throw new ArgumentException($"Failed to remove previous installation directory {pluginBase}");
                    }
                }

                // Clear file lists, they are assigned during the installation
                plugin.DwcFiles.Clear();
                plugin.RrfFiles.Clear();
                plugin.SbcFiles.Clear();
                _logger.Info("Installing files");

                // Make plugin directory
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
                        fileName = Path.Combine(Settings.BaseDirectory, entry.FullName[4..]);
                        plugin.RrfFiles.Add(entry.FullName[4..]);
                    }
                    else
                    {
                        // Put other files into <PluginDirectory>/<PluginName>/
                        fileName = Path.Combine(pluginBase, entry.FullName);

                        // Check what type of file this is
                        if (entry.FullName.StartsWith("www/"))
                        {
                            plugin.DwcFiles.Add(entry.FullName[4..]);
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

                    // Make program binaries executable
                    if (entry.FullName.StartsWith("bin/"))
                    {
                        _logger.Debug("Changing mode of {0} to 750", fileName);
                        LinuxApi.Commands.Chmod(fileName,
                            LinuxApi.UnixPermissions.Read | LinuxApi.UnixPermissions.Write | LinuxApi.UnixPermissions.Execute,
                            LinuxApi.UnixPermissions.Read | LinuxApi.UnixPermissions.Execute,
                            LinuxApi.UnixPermissions.None);
                    }
                }

                // Retrieve the SBC executable
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

                    if (!File.Exists(sbcExecutable))
                    {
                        throw new ArgumentException("SBC executable {0} not found", plugin.SbcExecutable);
                    }
                }

                // Install the web files. Try to use a symlink or copy the files if that failed
                if (plugin.DwcFiles.Count > 0)
                {
                    string pluginWwwPath = Path.Combine(pluginBase, "www");
                    string installWwwPath = Path.Combine(Settings.BaseDirectory, "www", plugin.Name);

                    bool copyDirectory = false;
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
                            copyDirectory = true;
                        }
                    }

                    if (copyDirectory)
                    {
                        _logger.Debug("Copying web files from {0} to {1}", pluginWwwPath, installWwwPath);
                        DirectoryCopy(pluginWwwPath, installWwwPath, true);
                    }
                }

                // Install refreshed plugin manifest
                _logger.Debug("Installing plugin manifest");
                string manifestFilename = Path.Combine(Settings.PluginDirectory, $"{plugin.Name}.json");
                using (FileStream manifestFile = new FileStream(manifestFilename, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(manifestFile, plugin, JsonHelper.DefaultJsonOptions);
                }
                _logger.Info("File installation complete");
            }

            // Plugin has been installed
            using (await Plugins.LockAsync())
            {
                Plugins.List.Add(plugin);
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

            Plugin plugin = new Plugin();
            using (Stream manifestStream = manifestFile.Open())
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

            // All OK
            return plugin;
        }

        /// <summary>
        /// Lock 
        /// </summary>
        private static readonly AsyncLock _packageLock = new AsyncLock();

        /// <summary>
        /// Install a Linux package
        /// </summary>
        /// <param name="package">Name of the package to install</param>
        private static async Task InstallPackage(string package)
        {
            if (!Program.IsRoot)
            {
                throw new ArgumentException("Cannot install packages as regular user");
            }

            using (await _packageLock.LockAsync(Program.CancellationToken))
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = Settings.InstallPackageCommand,
                    Arguments = Settings.InstallPackageArguments.Replace("{package}", package)
                };
                foreach (var kv in Settings.InstallPackageEnvironment)
                {
                    startInfo.EnvironmentVariables.Add(kv.Key, kv.Value);
                }

                using Process process = Process.Start(startInfo);
                await process.WaitForExitAsync(Program.CancellationToken);
                if (process.ExitCode != 0)
                {
                    throw new ArgumentException($"Failed to install package {package}, package manager exited with code {process.ExitCode}");
                }
            }
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
