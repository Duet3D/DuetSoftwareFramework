using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetAPIClient;
using DuetPluginService.Commands;
using DuetPluginService.IPC;
using DuetPluginService.PermissionManagers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DuetPluginService;

/// <summary>
/// Service that manages plugins for the Duet Plugin Service
/// </summary>
/// <param name="commandFactory">Command factory to create commands</param>
/// <param name="permissionManager">Permission manager</param>
/// <param name="pluginStore">Plugin store</param>
/// <param name="logger">Logger</param>
/// <param name="settings">Settings</param>
public sealed class PluginService(CommandFactory commandFactory, IPermissionManager permissionManager, PluginStore pluginStore, ILogger<PluginService> logger, IOptions<Settings> settings) : IHostedService
{
    /// <summary>
    /// Register installed plugins
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        List<Plugin> loadedPlugins = [];
        foreach (string file in Directory.GetFiles(settings.Value.PluginDirectory))
        {
            if (file.EndsWith(".json"))
            {
                try
                {
                    await using FileStream manifestStream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using JsonDocument manifestJson = await JsonDocument.ParseAsync(manifestStream, cancellationToken: cancellationToken);
                    Plugin plugin = new();
                    plugin.UpdateFromJson(manifestJson.RootElement, false);
                    plugin.Pid = -1;
                    using (await pluginStore.LockAsync(cancellationToken))
                    {
                        pluginStore.Plugins.Add(plugin);
                    }
                    loadedPlugins.Add(plugin);
                    logger.LogInformation("Plugin {Plugin} loaded", plugin.Id);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Failed to load plugin manifest {File}", Path.GetFileName(file));
                }
            }
        }

        // Regenerate any AppArmor profile that is missing from disk. The DSF package postinst removes the dsf.* profile
        // files on upgrade, so this loop runs once after each package update to rebuild them from the current template
        if (Environment.IsPrivilegedProcess && !settings.Value.DisableAppArmor && loadedPlugins.Count > 0)
        {
            List<Plugin> missingProfiles = [];
            foreach (Plugin plugin in loadedPlugins)
            {
                if (plugin.SbcPermissions.HasFlag(SbcPermissions.SuperUser))
                {
                    continue;
                }
                string profilePath = Path.Combine(settings.Value.AppArmorProfileDirectory, $"dsf.{plugin.Id}");
                if (!File.Exists(profilePath))
                {
                    missingProfiles.Add(plugin);
                }
            }

            if (missingProfiles.Count == 0)
            {
                return;
            }

            string? sdPath = null;
            try
            {
                using CommandConnection connection = new();
                await connection.ConnectAsync(settings.Value.SocketPath, cancellationToken);
                sdPath = await connection.ResolvePathAsync("0:/", cancellationToken);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Could not resolve SD path; skipping AppArmor profile regeneration");
                return;
            }

            foreach (Plugin plugin in missingProfiles)
            {
                try
                {
                    await permissionManager.InstallProfileAsync(plugin, settings.Value.PluginDirectory, sdPath, cancellationToken);
                    logger.LogInformation("Regenerated AppArmor profile for plugin {Plugin}", plugin.Id);
                }
                catch (Exception e)
                {
                    // Continue with the remaining plugins; StartPlugin refuses to launch any plugin whose profile is
                    // still missing on disk, so the failure here cannot result in an unconfined plugin
                    logger.LogError(e, "Failed to regenerate AppArmor profile for plugin {Plugin}", plugin.Id);
                }
            }
        }
    }

    /// <summary>
    /// Stop all services and clean up resources
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping plugins...");
        List<Task> stopTasks = [];
        using (await pluginStore.LockAsync(cancellationToken))
        {
            foreach (Plugin plugin in pluginStore.Plugins)
            {
                if (pluginStore.Processes.ContainsKey(plugin.Id))
                {
                    StopPlugin stopCommand = commandFactory.Create<StopPlugin>();
                    stopCommand.Plugin = plugin.Id;
                    stopTasks.Add(Task.Run(async () => await stopCommand.ExecuteAsync(cancellationToken), cancellationToken));
                }
            }
        }
        await Task.WhenAll(stopTasks);
        logger.LogInformation("Plugins stopped");
    }
}