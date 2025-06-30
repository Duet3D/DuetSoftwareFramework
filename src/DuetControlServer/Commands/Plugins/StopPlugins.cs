using DuetAPI.ObjectModel;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.StopPlugins"/> command
/// </summary>
/// <param name="commandFactory">Command factory</param>
/// <param name="model">Object model</param>
/// <param name="settings">Settings</param>
public sealed class StopPlugins(CommandFactory commandFactory, Model.ObjectModel model, IOptions<Settings> settings) : DuetAPI.Commands.StopPlugins
{
    /// <summary>
    /// Indicates if the plugins are being started
    /// </summary>
    private static readonly AsyncLock _stopLock = new();

    /// <summary>
    /// Stop all the plugins
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (!settings.Value.PluginSupport)
        {
            return;
        }

        using (await _stopLock.LockAsync(cancellationToken))
        {
            // Don't proceed if all the plugins have been stopped
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                if (!model.State.PluginsStarted)
                {
                    return;
                }
            }

            // Stop all plugins
            StringBuilder startedPlugins = new();
            List<Task> stopTasks = [];
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                foreach (Plugin item in model.Plugins.Values)
                {
                    if (item.Pid >= 0)
                    {
                        startedPlugins.AppendLine(item.Id);

                        if (item.Pid > 0)
                        {
                            StopPlugin stopCommand = commandFactory.Create<StopPlugin>();
                            stopCommand.Plugin = item.Id;
                            stopCommand.SaveState = false;
                            stopCommand.StoppingAll = true;
                            stopTasks.Add(stopCommand.ExecuteAsync(cancellationToken));
                        }
                    }
                }
            }

            try
            {
                await Task.WhenAll(stopTasks);
            }
            catch (SocketException)
            {
                // Can be expected when the remote service is terminated too early
            }

            using (await model.AccessReadWriteAsync(cancellationToken))
            {
                // Plugins have been stopped
                model.State.PluginsStarted = false;
            }
        }
    }
}
