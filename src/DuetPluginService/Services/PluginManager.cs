using DuetAPI.ObjectModel;
using DuetPluginService.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DuetPluginService.Services;

/// <summary>
/// Storage class for registered plugins
/// </summary>
/// <param name="logger">Logger</param>
/// <param name="settings">Application settings</param>
public sealed class PluginManager(ILogger<PluginManager> logger, IOptions<Settings> settings, IServiceProvider serviceProvider) : IHostedService
{
    private readonly AsyncLock _lock = new();
    private readonly Settings _settings = settings.Value;

    /// <summary>
    /// Lock access to the plugins
    /// </summary>
    /// <returns>Lock instance</returns>
    public IDisposable Lock() => _lock.Lock();

    /// <summary>
    /// Lock access to the plugins asynchronously
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Lock instance</returns>
    public AwaitableDisposable<IDisposable> LockAsync(CancellationToken cancellationToken) => _lock.LockAsync(cancellationToken);

    /// <summary>
    /// List of plugins
    /// </summary>
    public List<Plugin> Plugins { get; } = [];

    /// <summary>
    /// Plugin IDs vs processes
    /// </summary>
    public Dictionary<string, Process> Processes { get; } = [];

    /// <summary>
    /// Start the plugin manager service by loading all the plugin manifests
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (string file in Directory.GetFiles(_settings.PluginDirectory))
        {
            if (file.EndsWith(".json"))
            {
                try
                {
                    await using FileStream manifestStream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using JsonDocument manifestJson = await JsonDocument.ParseAsync(manifestStream);
                    Plugin plugin = new();
                    plugin.UpdateFromJson(manifestJson.RootElement, false);
                    plugin.Pid = -1;
                    using (await LockAsync(cancellationToken))
                    {
                        Plugins.Add(plugin);
                    }
                    logger.LogInformation("Plugin {Id} loaded", plugin.Id);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Failed to load plugin manifest {File}", Path.GetFileName(file));
                }
            }
        }
    }

    /// <summary>
    /// Stop all started plugins
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        List<Task> stopTasks = [];
        using (await LockAsync(cancellationToken))
        {
            foreach (Plugin plugin in Plugins)
            {
                if (Processes.ContainsKey(plugin.Id))
                {
                    StopPlugin stopCommand = ActivatorUtilities.CreateInstance<StopPlugin>(serviceProvider);
                    stopCommand.Plugin = plugin.Id;
                    stopTasks.Add(stopCommand.ExecuteAsync(cancellationToken));
                }
            }
        }
        await Task.WhenAll(stopTasks);
    }
}
