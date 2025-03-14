using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetPluginService.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DuetPluginService.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.StopPlugin"/> command
/// </summary>
/// <param name="pluginManager">Plugin manager</param>
/// <param name="loggerFactory">Logger factory</param>
public sealed class StopPlugin(PluginManager pluginManager, ILoggerFactory loggerFactory, IOptions<Settings> settings) : DuetAPI.Commands.StopPlugin
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
        ILogger logger = loggerFactory.CreateLogger($"Plugin {Plugin}");

        using (await pluginManager.LockAsync(cancellationToken))
        {
            // Try to find the plugin first
            Plugin? plugin = null;
            foreach (Plugin item in pluginManager.Plugins)
            {
                if (item.Id == Plugin)
                {
                    plugin = item;
                    break;
                }
            }

            if (plugin is null)
            {
                throw new ArgumentException($"Plugin {Plugin} not found by {(Utility.IsRoot ? "root service" : "service")}");
            }

            // Is this the right service to start the plugin?
            if (plugin.SbcPermissions.HasFlag(SbcPermissions.SuperUser) != Utility.IsRoot)
            {
                throw new InvalidOperationException("Wrong plugin service to start this plugin");
            }

            // Try to stop the process
            if (pluginManager.Processes.TryGetValue(plugin.Id, out Process? process) && !process.HasExited)
            {
                try
                {
                    // Ask process to terminate
                    logger.LogInformation("Attempting to stop process (pid {Pid})...", process.Id);
                    LinuxApi.Commands.Kill(process.Id, LinuxApi.Signal.SIGTERM);

                    // Wait a moment. Do not link this CTS to the main CTS because we may be shutting down at this point
                    using CancellationTokenSource timeoutCts = new(_settings.StopTimeout);
                    await process.WaitForExitAsync(timeoutCts.Token);

                    // Process terminated
                    logger.LogInformation("Process stopped by SIGTERM");
                }
                catch (OperationCanceledException)
                {
                    // Kill it and any potentially left-over child processes
                    process.Kill(true);
                    logger.LogInformation("Process killed");
                }
            }
        }
    }
}
