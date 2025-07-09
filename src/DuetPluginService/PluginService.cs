using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetPluginService.Commands;
using DuetPluginService.IPC;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DuetPluginService;

/// <summary>
/// Service that manages plugins for the Duet Plugin Service
/// </summary>
/// <param name="commandFactory">Command factory to create commands</param>
/// <param name="pluginStore">Plugin store</param>
/// <param name="logger">Logger</param>
/// <param name="settings">Settings</param>
public sealed class PluginService(CommandFactory commandFactory, PluginStore pluginStore, ILogger<PluginService> logger, IOptions<Settings> settings) : IHostedService
{
    /// <summary>
    /// Register installed plugins
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
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
                    logger.LogInformation("Plugin {Plugin} loaded", plugin.Id);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Failed to load plugin manifest {File}", Path.GetFileName(file));
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