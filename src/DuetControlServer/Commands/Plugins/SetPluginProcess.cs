using DuetAPI.ObjectModel;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.InstallPlugin"/> command
/// </summary>
/// <param name="commandFactory">Command factory</param>
/// <param name="model">Object model</param>
/// <param name="settings">Settings</param>
public sealed class SetPluginProcess(CommandFactory commandFactory, Model.ObjectModel model, IOptions<Settings> settings) : DuetAPI.Commands.SetPluginProcess
{
    /// <summary>
    /// Event that is set when a plugin has stopped
    /// </summary>
    public static readonly AsyncAutoResetEvent PluginStoppedEvent = new(false);

    /// <summary>
    /// Logger instance
    /// </summary>
    private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Update the pid of a given plugin
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (!settings.Value.PluginSupport)
        {
            throw new NotSupportedException("Plugin support has been disabled");
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (model.Plugins.TryGetValue(Plugin, out Plugin plugin))
            {
                if (plugin.Pid > 0 && Pid < 0 && plugin.SbcAutoRestart)
                {
                    _ = Task.Run(async delegate
                    {
                        try
                        {
                            // Wait a moment to avoid excessive system load in case the plugin is broken
                            await Task.Delay(settings.Value.PluginAutoRestartInterval, cancellationToken);

                            // Restart it
                            _logger.Info("Auto-restarting plugin {0}", Plugin);
                            StartPlugin startPlugin = commandFactory.Create<StartPlugin>();
                            startPlugin.Plugin = Plugin;
                            await startPlugin.ExecuteAsync(cancellationToken);
                        }
                        catch (Exception e)
                        {
                            if (e is not OperationCanceledException)
                            {
                                _logger.Error(e, "Failed to auto-restart plugin {0}", Plugin);
                            }
                        }
                    }, cancellationToken);
                }
                plugin.Pid = Pid;
                plugin.Started = Pid > 0 && !plugin.SbcNotifyStarted;
                if (!plugin.Started)
                {
                    PluginStoppedEvent.Set();
                }
            }
            else
            {
                throw new ArgumentException($"Plugin {Plugin} not found");
            }
        }
    }
}
