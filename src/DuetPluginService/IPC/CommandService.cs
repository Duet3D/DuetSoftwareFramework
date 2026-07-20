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
/// <param name="lifetime">Application lifetime used to shut down cleanly when DCS is unavailable</param>
/// <param name="logger">Logger</param>
public class CommandService(PluginServiceConnection connection, IHostApplicationLifetime lifetime, ILogger<CommandService> logger) : BackgroundService
{
    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Connect to DCS
        try
        {
            await connection.ConnectAsync(cancellationToken);
        }
        catch (SocketException e)
        {
            // DCS isn't up yet (we may be starting in parallel with it). Keep the log short and let systemd
            // restart us to retry; the full stack trace is only emitted at debug level
            logger.LogDebug(e, "Failed to connect to DCS");
            logger.LogError("Failed to connect to DCS: {Message}", e.Message);
            lifetime.StopApplication();
            return;
        }

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

        while (!stoppingToken.IsCancellationRequested)
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
                            logger.LogError("Cannot execute {Command}: {Message}", command!.Command, e.Message);
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
