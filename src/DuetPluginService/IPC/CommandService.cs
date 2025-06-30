using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Threading;
using System;
using System.Net.Sockets;
using DuetAPI.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DuetPluginService.Commands;

namespace DuetPluginService.IPC;

/// <summary>
/// Service which interacts with DCS to perform plugin-specific tasks
/// </summary>
/// <param name="connection">Plugin service connection</param>
/// <param name="pluginStore">Plugin store</param>
/// <param name="hostEnvironment">Host environment</param>
/// <param name="logger">Logger</param>
public class CommandService(CommandFactory commandActivator, PluginServiceConnection connection, PluginStore pluginStore, IHostEnvironment hostEnvironment, ILogger<CommandService> logger) : BackgroundService
{
    /// <summary>
    /// Start the main service
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>Asynchronous task</returns>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Load available plugin manifests
        foreach (string file in Directory.GetFiles(hostEnvironment.ContentRootPath, "*.json"))
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
                logger.LogInformation("Plugin {Id} loaded", plugin.Id);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to load plugin manifest {File}", Path.GetFileName(file));
            }
        }

        // Connect to DCS
        await connection.ConnectAsync(cancellationToken);

        // Start the main service
        await base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Keep processing incoming commands until the service is stopped
    /// </summary>
    /// <param name="stoppingToken">Cancellation token to invoke when the service is supposed to stop</param>
    /// <returns>Asynchronous task</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DuetAPI.Commands.BaseCommand? command = null;
        Type commandType;

        for (;;)
        {
            try
            {
                // Read another command from the IPC connection
                command = await connection.ReceiveCommandAsync(stoppingToken);
                commandType = command.GetType();

                // Execute it and send back the result
                object? result = await command.InvokeAsync(stoppingToken);
                await connection.SendResponseAsync(result, stoppingToken);

                // Shut down the socket if this was the last command
                if (stoppingToken.IsCancellationRequested)
                {
                    connection.Close();
                    break;
                }
            }
            catch (SocketException)
            {
                // Connection has been terminated
                break;
            }
            catch (Exception e)
            {
                // Send errors back to the client
                if (e is not OperationCanceledException)
                {
                    if (command is not null)
                    {
                        if (e is UnauthorizedAccessException)
                        {
                            logger.LogError("Insufficient permissions to execute {Command}", command!.Command);
                        }
                        else
                        {
                            logger.LogError(e, "Failed to execute {Command}", command.Command);
                        }
                    }
                    else
                    {
                        logger.LogError(e, "Failed to execute command");
                    }
                }
                await connection.SendResponseAsync(e, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Stop the main service and all started plugins
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop the main service
        await base.StopAsync(cancellationToken);

        // Stop all started plugins
        List<Task> stopTasks = [];
        using (await pluginStore.LockAsync(cancellationToken))
        {
            foreach (Plugin plugin in pluginStore.Plugins)
            {
                if (pluginStore.Processes.ContainsKey(plugin.Id))
                {
                    IPC.StopPlugin stopPlugin = commandActivator.Create<IPC.StopPlugin>();
                    stopPlugin.Plugin = plugin.Id;
                    stopTasks.Add(stopPlugin.ExecuteAsync(cancellationToken));
                }
            }
        }
        await Task.WhenAll(stopTasks);
    }
}