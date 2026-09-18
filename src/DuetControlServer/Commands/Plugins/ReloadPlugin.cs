using DuetAPI.ObjectModel;
using DuetControlServer.IPC.Processors;
using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.ReloadPlugin"/> command
/// </summary>
/// <param name="model">Object model</param>
/// <param name="settings">Settings</param>
public sealed class ReloadPlugin(Model.ObjectModel model, IOptions<Settings> settings) : DuetAPI.Commands.ReloadPlugin
{
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

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            // The plugin must be stopped at this point
            if (model.Plugins.TryGetValue(Plugin, out Plugin plugin) && plugin.Pid > 0)
            {
                throw new ArgumentException("Plugin must be stopped before its manifest can be reloaded");
            }

            // Update the plugin manifest
            string file = Path.Combine(settings.Value.PluginDirectory, Plugin + ".json");
            if (File.Exists(file))
            {
                if (plugin is null)
                {
                    plugin = new();
                    model.Plugins.Add(Plugin, plugin);
                }

                await using FileStream manifestStream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, settings.Value.FileBufferSize);
                using JsonDocument manifestJson = await JsonDocument.ParseAsync(manifestStream, cancellationToken: cancellationToken);
                plugin.UpdateFromJson(manifestJson.RootElement);
                plugin.Pid = -1;
                plugin.Started = false;
            }
            else
            {
                if (plugin is null)
                {
                    // Don't attempt to remove a non-existent plugin
                    return;
                }
                model.Plugins.Remove(Plugin);
            }
        }

        // Reload the plugin via the plugin services
        await PluginService.PerformCommandAsync(this, true, cancellationToken);
        await PluginService.PerformCommandAsync(this, false, cancellationToken);
    }
}
