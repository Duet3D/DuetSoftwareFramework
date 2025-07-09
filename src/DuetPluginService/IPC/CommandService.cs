using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Threading;
using System;
using System.Net.Sockets;

namespace DuetPluginService.IPC;

/// <summary>
/// Service which interacts with DCS to perform plugin-specific tasks
/// </summary>
/// <param name="connection">Plugin service connection</param>
/// <param name="logger">Logger</param>
public class CommandService(PluginServiceConnection connection, ILogger<CommandService> logger) : BackgroundService
{
    /// <summary>
    /// Start the main service
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>Asynchronous task</returns>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
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

        for (; ; )
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
}