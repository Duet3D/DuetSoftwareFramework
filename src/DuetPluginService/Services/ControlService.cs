using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Threading;
using System;
using System.Net.Sockets;

namespace DuetPluginService.Services;

/// <summary>
/// Main service which interacts with DCS to perform plugin-specific tasks
/// </summary>
/// <param name="logger">Logger</param>
/// <param name="settings">Application settings</param>
public class ControlService(PluginServiceConnection connection, ILogger<ControlService> logger) : BackgroundService
{
    /// <summary>
    /// Lifecycle of this service
    /// </summary>
    /// <param name="stoppingToken">Cancellation token to invoke when the service is supposed to stop</param>
    /// <returns>Asynchronous task</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DuetAPI.Commands.BaseCommand? command = null;
        Type commandType;
        do
        {
            try
            {
                // Read another command from the IPC connection
                command = await connection.ReceiveCommandAsync(stoppingToken);
                commandType = command.GetType();

                // Execute it and send back the result
                object? result = await command.InvokeAsync();
                await connection.SendResponseAsync(result, stoppingToken);

                // Shut down the socket if this was the last command
                if (stoppingToken.IsCancellationRequested)
                {
                    connection.Close();
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
                await connection.SendResponseAsync(e);
            }
        }
        while (!stoppingToken.IsCancellationRequested);
    }
}