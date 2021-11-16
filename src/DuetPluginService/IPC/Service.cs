using System;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace DuetPluginService.IPC
{
    /// <summary>
    /// General service for IPC
    /// </summary>
    public static class Service
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// IPC connection to DCS
        /// </summary>
        private static readonly PluginServiceConnection _connection = new();

        /// <summary>
        /// Connect to DCS after the first start
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static Task Connect() => _connection.Connect(Settings.SocketPath);

        /// <summary>
        /// Lifecycle of this service
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Run()
        {
            DuetAPI.Commands.BaseCommand command = null;
            Type commandType;
            do
            {
                try
                {
                    // Read another command from the IPC connection
                    command = await _connection.ReceiveCommand();
                    commandType = command.GetType();

                    // Execute it and send back the result
                    object result = await command.Invoke();
                    await _connection.SendResponse(result);

                    // Shut down the socket if this was the last command
                    if (Program.CancellationToken.IsCancellationRequested)
                    {
                        _connection.Close();
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
                        if (e is UnauthorizedAccessException)
                        {
                            _logger.Error("Insufficient permissions to execute {0}", command.Command);
                        }
                        else if (command != null)
                        {
                            _logger.Error(e, "Failed to execute {0}", command.Command);
                        }
                        else
                        {
                            _logger.Error(e, "Failed to execute command");
                        }
                    }
                    await _connection.SendResponse(e);
                }
            }
            while (!Program.CancellationToken.IsCancellationRequested);
        }
    }
}
