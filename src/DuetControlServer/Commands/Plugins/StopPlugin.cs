using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.IPC.Processors;
using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.StopPlugin"/> command
/// </summary>
/// <param name="model">Object model</param>
/// <param name="settings">Settings</param>
public sealed class StopPlugin(Model.ObjectModel model, IOptions<Settings> settings) : DuetAPI.Commands.StopPlugin
{
    /// <summary>
    /// Stop a plugin
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

        bool stopPlugin = false, asRoot = false;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (model.Plugins.TryGetValue(Plugin, out Plugin plugin))
            {
                if (plugin.Pid > 0)
                {
                    // Make sure no other running plugin depends on it
                    if (!StoppingAll)
                    {
                        foreach (Plugin other in model.Plugins.Values)
                        {
                            if (other.Id != Plugin && other.Pid > 0 && other.SbcPluginDependencies.Contains(Plugin))
                            {
                                throw new ArgumentException($"Cannot stop plugin because plugin {other.Id} depends on it");
                            }
                        }
                    }

                    // Stop the plugin
                    plugin.Pid = 0;
                    plugin.Started = false;
                    stopPlugin = true;
                    asRoot = plugin.SbcPermissions.HasFlag(SbcPermissions.SuperUser);
                }
            }
            else
            {
                throw new ArgumentException($"Plugin {Plugin} not found");
            }
        }

        if (stopPlugin)
        {
            // Stop it via the plugin service. This will reset the PID to -1 too
            await PluginService.PerformCommandAsync(this, asRoot, cancellationToken);
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
                    if (item.Pid >= 0 && item.Id != Plugin)
                    {
                        await writer.WriteLineAsync(item.Id);
                    }
                }
            }
        }
    }

    /// <summary>
    /// This is set to true if all the plugins are supposed to be stopped
    /// </summary>
    [JsonIgnore]
    public bool StoppingAll { get; set; }
}
