using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using Nito.AsyncEx;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
        private NLog.Logger? _logger;

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
            _logger = NLog.LogManager.GetLogger($"Plugin {plugin.Id}");

            if (Program.IsRoot)
            {
                // Run preinstall routine if needed
                if (plugin.SbcPackageDependencies.Count > 0 && !string.IsNullOrEmpty(Settings.PreinstallPackageCommand))
                {
                    _logger.Info("Running preinstall command");
                    using Process process = Process.Start(Settings.PreinstallPackageCommand, Settings.PreinstallPackageArguments);
                    await process.WaitForExitAsync(Program.CancellationToken);
                }

                // Install plugin dependencies
                foreach (string package in plugin.SbcPackageDependencies)
                {
                    _logger.Info("Installing package {0}", package);
                    await InstallPackage(package);
                }

                foreach (string package in plugin.SbcPythonDependencies)
                {
                    _logger.Info("Installing Python package {0}", package);
                    await InstallPythonPackage(package);
                }

                // Apply security profile for this plugin unless it gets root permissions anyway
                if (!plugin.SbcPermissions.HasFlag(SbcPermissions.SuperUser))
                {
                    await Permissions.Manager.InstallProfile(plugin);
                    _logger.Info("Security profile installed");
                }
            }
            else
            {
                // Delete old files
                string pluginBase = Path.Combine(Settings.PluginDirectory, plugin.Id);
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
                plugin.DsfFiles.Clear();
                plugin.DwcFiles.Clear();
                plugin.SdFiles.Clear();
                _logger.Info("Installing files");

                // Make plugin directory
                if (!Directory.Exists(pluginBase))
                {
                    _logger.Debug("Creating plugin base directory {0}", pluginBase);
                    Directory.CreateDirectory(pluginBase);
                }

                // Install new plugin files
                string architecture = RuntimeInformation.OSArchitecture switch
                {
                    Architecture.Arm => "arm",
                    Architecture.Arm64 => "arm64",
                    Architecture.X86 => "x86",
                    Architecture.X64 => "x86_64",
                    _ => "unknown"
                };

                foreach (ZipArchiveEntry entry in zipArchive.Entries)
                {
                    // Ignore plugin.json, it will be written when this archive has been extracted
                    // Also ignore directories, they are automatically created below
                    if (entry.FullName == "plugin.json" || entry.FullName.EndsWith('/'))
                    {
                        continue;
                    }

                    string fileName;
                    if (entry.FullName.StartsWith("dsf/"))
                    {
                        // Put DSF plugin files into <PluginDirectory>/<PluginName>/dsf
                        fileName = Path.Combine(pluginBase, entry.FullName);
                        plugin.DsfFiles.Add(entry.FullName[4..]);
                    }
                    else if (entry.FullName.StartsWith("dwc/"))
                    {
                        // Put DWC plugin files into <PluginDirectory>/<PluginName>/dwc
                        fileName = Path.Combine(pluginBase, entry.FullName);
                        plugin.DwcFiles.Add(entry.FullName[4..]);
                    }
                    else if (entry.FullName.StartsWith("sd/"))
                    {
                        // Put SD files into 0:/
                        fileName = Path.Combine(Settings.BaseDirectory, entry.FullName[3..]);
                        plugin.SdFiles.Add(entry.FullName[3..]);
                    }
                    else
                    {
                        // Skip other files
                        _logger.Warn("Skipping installation of file {0}", entry.FullName);
                        continue;
                    }

                    // Make sure the parent directory exists
                    string parentDirectory = Path.GetDirectoryName(fileName)!;
                    if (!Directory.Exists(parentDirectory))
                    {
                        _logger.Debug("Creating new directory {0}", parentDirectory);
                        Directory.CreateDirectory(parentDirectory);
                    }

                    // Extract the file
                    if (File.Exists(fileName) && plugin.SbcConfigFiles.Any(file => fileName == Path.Combine(Settings.BaseDirectory, "sys", file) || fileName == Path.Combine(Settings.BaseDirectory, file)))
                    {
                        _logger.Debug("Not overwriting config file {0}", entry.FullName);
                    }
                    else
                    {
                        _logger.Debug("Extracting {0} to {1}", entry.FullName, fileName);
                        await using FileStream fileStream = new(fileName, FileMode.Create, FileAccess.Write, FileShare.None);
                        await using Stream zipFileStream = entry.Open();
                        await zipFileStream.CopyToAsync(fileStream);

                        // Make program binaries executable
                        if (!string.IsNullOrEmpty(plugin.SbcExecutable) &&
                            (entry.FullName == "dsf/" + plugin.SbcExecutable || entry.FullName == $"dsf/{architecture}/{plugin.SbcExecutable}" ||
                             plugin.SbcExtraExecutables.Any(executable => (entry.FullName == "dsf/" + executable) || (entry.FullName == $"dsf/{architecture}/{executable}"))))
                        {
                            _logger.Debug("Changing mode of {0} to 770", fileName);
                            LinuxApi.Commands.Chmod(fileName,
                                LinuxApi.UnixPermissions.Write | LinuxApi.UnixPermissions.Read | LinuxApi.UnixPermissions.Execute,
                                LinuxApi.UnixPermissions.Write | LinuxApi.UnixPermissions.Read | LinuxApi.UnixPermissions.Execute,
                                LinuxApi.UnixPermissions.None);
                        }
                    }
                }

                // Retrieve the SBC executable
                if (!string.IsNullOrEmpty(plugin.SbcExecutable))
                {
                    string sbcExecutable = Path.Combine(pluginBase, "dsf", architecture, plugin.SbcExecutable);
                    if (!File.Exists(sbcExecutable))
                    {
                        sbcExecutable = Path.Combine(pluginBase, "dsf", plugin.SbcExecutable);
                    }

                    if (!File.Exists(sbcExecutable))
                    {
                        throw new ArgumentException($"SBC executable {plugin.SbcExecutable} not found");
                    }
                }

                // Install the web files. Try to use a symlink or copy the files if that failed
                foreach (string dwcFile in plugin.DwcFiles)
                {
                    string pluginWwwPath = Path.Combine(pluginBase, "dwc", dwcFile);
                    string installWwwPath = Path.Combine(Settings.BaseDirectory, "www", dwcFile);

                    // Create parent directory first
                    string directory = Path.GetDirectoryName(installWwwPath)!;
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

#if true
# if NET_7_0_OR_GREATER
#  warning check if this is fixed in ASP.NET 7
# endif
                    // Copy the file. ASP.NET 5 and 6 do not perform lstat on symlinks so files served from symlinks are always truncated.
                    // It seems like .NET 6 also treats symlinks as open files for some reason, check if this is still the case in .NET 7 or later
                    _logger.Debug("Copying {0} -> {1}", pluginWwwPath, installWwwPath);
                    File.Copy(pluginWwwPath, installWwwPath, true);
#else
                    // Attempt to symlink or copy the file
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
                            _logger.Warn("Failed to create symlink to web directory, trying to copy web file instead...");
                            File.Copy(pluginWwwPath, installWwwPath);
                        }
                    }
#endif
                }

                // Install refreshed plugin manifest
                _logger.Debug("Installing plugin manifest");
                string manifestFilename = Path.Combine(Settings.PluginDirectory, $"{plugin.Id}.json");
                await using (FileStream manifestFile = new(manifestFilename, FileMode.Create, FileAccess.Write, FileShare.None))
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
            ZipArchiveEntry? manifestFile = zipArchive.GetEntry("plugin.json");
            if (manifestFile is null)
            {
                throw new ArgumentException("plugin.json not found in the ZIP file");
            }

            Plugin plugin = new();
            await using (Stream manifestStream = manifestFile.Open())
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

        /// <summary>
        /// Lock for installing system packages
        /// </summary>
        private static readonly AsyncLock _packageLock = new();

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
                ProcessStartInfo startInfo = new()
                {
                    FileName = Settings.InstallPackageCommand,
                    Arguments = Settings.InstallPackageArguments.Replace("{package}", package)
                };
                foreach (var kv in Settings.InstallPackageEnvironment)
                {
                    startInfo.EnvironmentVariables.Add(kv.Key, kv.Value);
                }

                using Process? process = Process.Start(startInfo);
                if (process is not null)
                {
                    await process.WaitForExitAsync(Program.CancellationToken);
                    if (process.ExitCode != 0)
                    {
                        throw new ArgumentException($"Failed to install package {package}, package manager exited with code {process.ExitCode}");
                    }
                }
            }
        }

        /// <summary>
        /// Install a Python package
        /// </summary>
        /// <param name="package">Name of the package to install</param>
        private static async Task InstallPythonPackage(string package)
        {
            if (!Program.IsRoot)
            {
                throw new ArgumentException("Cannot install packages as regular user");
            }

            using (await _packageLock.LockAsync(Program.CancellationToken))
            {
                ProcessStartInfo startInfo = new()
                {
                    FileName = Settings.InstallPythonPackageCommand,
                    Arguments = Settings.InstallPythonPackageArguments.Replace("{package}", package)
                };
                foreach (var kv in Settings.InstallPackageEnvironment)
                {
                    startInfo.EnvironmentVariables.Add(kv.Key, kv.Value);
                }

                using Process? process = Process.Start(startInfo);
                if (process is not null)
                {
                    await process.WaitForExitAsync(Program.CancellationToken);
                    if (process.ExitCode != 0)
                    {
                        throw new ArgumentException($"Failed to install package {package}, package manager exited with code {process.ExitCode}");
                    }
                }
            }
        }
    }
}
