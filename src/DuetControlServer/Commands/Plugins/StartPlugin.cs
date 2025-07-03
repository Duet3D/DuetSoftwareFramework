using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.IPC.Processors;
using DuetSharedLibrary;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.StartPlugin"/> command
/// </summary>
/// <param name="commandFactory">Command factory</param>
/// <param name="model">Object model</param>
/// <param name="settings">Settings</param>
public sealed class StartPlugin(CommandFactory commandFactory, Model.ObjectModel model, IOptions<Settings> settings) : DuetAPI.Commands.StartPlugin
{
    /// <summary>
    /// Lock to be used when a plugin is started to avoid race conditions
    /// </summary>
    private static readonly AsyncLock _startLock = new();

    /// <summary>
    /// Start a plugin
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="ArgumentException">Plugin is invalid</exception>
    public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (!settings.Value.PluginSupport)
        {
            throw new NotSupportedException("Plugin support has been disabled");
        }

        // Start the plugin and its dependencies
        using (await _startLock.LockAsync(cancellationToken))
        {
            await StartAsync(Plugin, cancellationToken: cancellationToken);
        }

        // Save the execution state if requested
        if (SaveState)
        {
            await using FileStream fileStream = new(settings.Value.PluginsFilename, FileMode.Create, FileAccess.Write, FileShare.None, settings.Value.FileBufferSize);
            await using StreamWriter writer = new(fileStream, Encoding.UTF8, settings.Value.FileBufferSize);
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                foreach (Plugin item in model.Plugins.Values)
                {
                    if (item.Pid >= 0)
                    {
                        await writer.WriteLineAsync(item.Id);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Start a plugin (as a dependency)
    /// </summary>
    /// <param name="id">Plugin identifier</param>
    /// <param name="requiredBy">Plugin that requires this plugin</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the plugin could be found</returns>
    private async Task StartAsync(string id, string? requiredBy = null, CancellationToken cancellationToken = default)
    {
        bool rootPlugin;
        List<string> dependencies = [];
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (model.Plugins.TryGetValue(Plugin, out Plugin plugin))
            {
                // Don't do anything if the plugin is already running or if it cannot be started on the SBC
                if (plugin.Pid > 0 || string.IsNullOrEmpty(plugin.SbcExecutable))
                {
                    return;
                }

                // Start plugin dependencies
                foreach (string dependency in plugin.SbcPluginDependencies)
                {
                    if (dependency != requiredBy)
                    {
                        dependencies.Add(dependency);
                    }
                }

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
                        string rrfVersion = model.Boards[0].FirmwareVersion;
                        if (!PluginManifest.CheckVersion(rrfVersion, plugin.RrfVersion))
                        {
                            throw new ArgumentException($"Incompatible RRF version (requires {plugin.RrfVersion}, got {rrfVersion})");
                        }
                    }
                    else
                    {
                        throw new ArgumentException("Failed to check RRF version");
                    }
                }

                // Got a plugin
                rootPlugin = plugin.SbcPermissions.HasFlag(SbcPermissions.SuperUser);
            }
            else
            {
                if (requiredBy is null)
                {
                    throw new ArgumentException($"Plugin {Plugin} not found");
                }
                throw new ArgumentException($"Dependency {id} of plugin {requiredBy} not found");
            }
        }

        // Start all the dependencies first
        foreach (string dependency in dependencies)
        {
            await StartAsync(dependency, id, cancellationToken);
        }

        // Start the plugin via the plugin service. This will update the PID too
        StartPlugin startCommand = commandFactory.Create<StartPlugin>();
        startCommand.Plugin = id;
        await PluginService.PerformCommandAsync(startCommand, rootPlugin, cancellationToken);
    }
}
