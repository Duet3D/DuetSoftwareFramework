using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetSharedLibrary;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DuetPluginService.IPC;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.StopPlugin"/> command
/// </summary>
/// <param name="pluginStore">Plugin store</param>
/// <param name="loggerFactory">Logger factory</param>
public sealed class StopPlugin(PluginStore pluginStore, ILoggerFactory loggerFactory, IOptions<Settings> settings) : DuetAPI.Commands.StopPlugin
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

            if (plugin is null)
            {
                throw new ArgumentException($"Plugin {Plugin} not found by {(Environment.IsPrivilegedProcess ? "root service" : "service")}");
            }

            // Is this the right service to start the plugin?
            if (plugin.SbcPermissions.HasFlag(SbcPermissions.SuperUser) != Environment.IsPrivilegedProcess)
            {
                throw new InvalidOperationException("Wrong plugin service to start this plugin");
            }

            // Try to stop the process
            if (pluginStore.Processes.TryGetValue(plugin.Id, out Process? process) && !process.HasExited)
            {
                try
                {
                    // Ask process to terminate
                    logger.LogInformation("Attempting to stop process (pid {Pid})...", process.Id);
                    process.Terminate();

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
