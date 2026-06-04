using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.IPC.Processors;
using DuetSharedLibrary;
using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.InstallPlugin"/> command
/// </summary>
/// <param name="commandFactory">Command factory</param>
/// <param name="model">Object model</param>
/// <param name="settings">Settings</param>
public sealed class InstallPlugin(CommandFactory commandFactory, Model.ObjectModel model, IOptions<Settings> settings) : DuetAPI.Commands.InstallPlugin
{
    /// <summary>
    /// Internal flag to indicate that custom plugin files should not be purged
    /// </summary>
    public bool Upgrade { get; set; }

    /// <summary>
    /// Install or upgrade a plugin
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="ArgumentException">Plugin is incompatible</exception>
    public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (!settings.Value.PluginSupport)
        {
            throw new NotSupportedException("Plugin support has been disabled");
        }
        if (settings.Value.DisablePluginInstallations)
        {
            throw new NotSupportedException("Installation of third-party plugins has been disabled");
        }

        Plugin plugin;
        using (ZipArchive zipArchive = ZipFile.OpenRead(PluginFile))
        {
            // Get the plugin manifest from the ZIP file
            plugin = await ExtractManifestAsync(zipArchive, cancellationToken);

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
        if (plugin.SbcPermissions.HasFlag(SbcPermissions.SuperUser) && !settings.Value.RootPluginSupport)
        {
            throw new ArgumentException("Installation of plugins with super-user permissions is not allowed");
        }

        // Validate the current DSF/RRF versions
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            // Check the required DSF version
            string version = VersionHelper.GetVersion();
            if (!PluginManifest.CheckVersion(version, plugin.SbcDsfVersion!))
            {
                throw new ArgumentException($"Incompatible DSF version (requires {plugin.SbcDsfVersion}, got {version})");
            }

            // Check the required RRF version
            if (!string.IsNullOrEmpty(plugin.RrfVersion))
            {
                if (model.Boards.Count > 0)
                {
                    if (!PluginManifest.CheckVersion(model.Boards[0].FirmwareVersion, plugin.RrfVersion))
                    {
                        throw new ArgumentException($"Incompatible RRF version (requires {plugin.RrfVersion}, got {model.Boards[0].FirmwareVersion})");
                    }
                }
                else
                {
                    throw new ArgumentException("Failed to check RRF version");
                }
            }
        }

        // Make sure all the required plugins dependencies are installed
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            foreach (string dependency in plugin.SbcPluginDependencies)
            {
                if (!model.Plugins.ContainsKey(dependency))
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
                if (!char.IsLetterOrDigit(c) && c != '.' && c != '-' && c != '_' && c != '+' && c != '<' && c != '>' && c != '=' && c != ',' && c != ':' && c != '/' && c != '@' && c != '#' && c != '~')
                {
                    throw new ArgumentException($"Illegal characters in required Python package {package}");
                }
            }
        }

        // Uninstall the old plugin (if applicable). Plugin ids are matched case-insensitively, so an
        // existing plugin installed under a different-cased id is treated as the same plugin. Uninstall
        // it by its stored id so the on-disk files (named after that id) are removed rather than orphaned
        string? installedId = null;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (model.Plugins.TryGetValue(plugin.Id, out Plugin? installedPlugin))
            {
                installedId = installedPlugin.Id;
            }
        }

        Upgrade = installedId is not null;
        if (installedId is not null)
        {
            UninstallPlugin uninstallCommand = commandFactory.Create<UninstallPlugin>();
            uninstallCommand.Plugin = installedId;
            uninstallCommand.ForUpgrade = true;
            await uninstallCommand.ExecuteAsync(cancellationToken);
        }

        // Forward this command to the plugin services
        // 1) Install regular files via dsf user
        // 2) Perform policy generation using AppArmor profiles via root
        await PluginService.PerformCommandAsync(this, false, cancellationToken);
        await PluginService.PerformCommandAsync(this, true, cancellationToken);

        // If possible, reload the plugin manifest with the updated file lists and register it in the object model
        string manifestFilename = Path.Combine(settings.Value.PluginDirectory, plugin.Id + ".json");
        if (File.Exists(manifestFilename))
        {
            await using (FileStream manifestStream = new(manifestFilename, FileMode.Open, FileAccess.Read, FileShare.Read, settings.Value.FileBufferSize))
            {
                using JsonDocument manifestJson = await JsonDocument.ParseAsync(manifestStream, cancellationToken: cancellationToken);
                plugin.UpdateFromJson(manifestJson.RootElement, false);
            }

            using (await model.AccessReadWriteAsync(cancellationToken))
            {
                model.Plugins.Add(plugin.Id, plugin);
            }
        }
    }

    /// <summary>
    /// Extract, parse, and verify the plugin manifest
    /// </summary>
    /// <param name="zipArchive">ZIP archive containing the plugin files</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Plugin manifest</returns>
    /// <exception cref="ArgumentException">Plugin is incompatible</exception>
    private static async Task<Plugin> ExtractManifestAsync(ZipArchive zipArchive, CancellationToken cancellationToken = default)
    {
        // Extract the plugin manifest
        ZipArchiveEntry? manifestFile = zipArchive.GetEntry("plugin.json") ?? throw new ArgumentException("plugin.json not found in the ZIP file");
        Plugin plugin = new();
        await using (Stream manifestStream = manifestFile.Open())
        {
            using JsonDocument manifestJson = await JsonDocument.ParseAsync(manifestStream, cancellationToken: cancellationToken);
            plugin.UpdateFromJson(manifestJson.RootElement, false);
        }
        plugin.Pid = -1;
        plugin.Started = false;

        // Check for reserved permissions
        if (plugin.SbcPermissions.HasFlag(SbcPermissions.ServicePlugins))
        {
            throw new ArgumentException("ServicePlugins permission is reserved for internal purposes");
        }

        // All OK
        return plugin;
    }
}
