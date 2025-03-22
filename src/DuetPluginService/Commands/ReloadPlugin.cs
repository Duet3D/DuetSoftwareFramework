using DuetAPI.ObjectModel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DuetPluginService.IPC;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.ReloadPlugin"/> command
/// </summary>
/// <param name="pluginStore">Plugin store</param>
/// <param name="hostEnvironment">Host environment</param>
/// <param name="settings">Application settings</param>
public sealed class ReloadPlugin(PluginStore pluginStore, IHostEnvironment hostEnvironment, IOptions<Settings> settings) : DuetAPI.Commands.ReloadPlugin
{
    private readonly Settings _settings = settings.Value;

    /// <summary>
    /// Stop a plugin
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="ArgumentException">Plugin is invalid</exception>
    public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        using (await pluginStore.LockAsync(cancellationToken))
        {
            // Try to find the plugin first
            Plugin? plugin = null;
            foreach (Plugin item in pluginStore.Plugins)
            {
                if (item.Id == Plugin)
                {
                    plugin = item;
                    break;
                }
            }

            // Update the plugin manifest
            string file = Path.Combine(hostEnvironment.ContentRootPath, Plugin + ".json");
            if (File.Exists(file))
            {
                if (plugin is null)
                {
                    plugin = new();
                    pluginStore.Plugins.Add(plugin);
                }

                await using FileStream manifestStream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                using JsonDocument manifestJson = await JsonDocument.ParseAsync(manifestStream);
                plugin.UpdateFromJson(manifestJson.RootElement, false);
                plugin.Pid = -1;
            }
            else
            {
                if (plugin is null)
                {
                    // Don't attempt to remove a non-existent plugin
                    return;
                }
                pluginStore.Plugins.Remove(plugin);
            }
        }
    }
}
