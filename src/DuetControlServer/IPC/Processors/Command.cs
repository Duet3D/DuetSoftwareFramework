using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Connection.InitMessages;
using DuetControlServer.Commands;

namespace DuetControlServer.IPC.Processors;

/// <summary>
/// Command interpreter for client requests
/// </summary>
public sealed class Command : IProcessor
{
    /// <summary>
    /// List of supported commands in this mode
    /// </summary>
    public static Type[] SupportedCommands { get; } =
    [
        typeof(GetFileInfo),
        typeof(ResolvePath),
        typeof(Code),
        typeof(EvaluateExpression),
        typeof(Flush),
        typeof(SimpleCode),
        typeof(WriteMessage),
        typeof(AddHttpEndpoint),
        typeof(RemoveHttpEndpoint),
        typeof(CheckPassword),
        typeof(GetObjectModel),
        typeof(LockObjectModel),
        typeof(PatchObjectModel),
        typeof(SetObjectModel),
        typeof(SetUpdateStatus),
        typeof(SyncObjectModel),
        typeof(UnlockObjectModel),
        typeof(InstallPlugin),
        typeof(NotifyPluginStarted),
        typeof(ReloadPlugin),
        typeof(SetNetworkProtocol),
        typeof(SetPluginData),
        typeof(SetPluginProcess),
        typeof(StartPlugin),
        typeof(StartPlugins),
        typeof(StopPlugin),
        typeof(StopPlugins),
        typeof(UninstallPlugin),
        typeof(AddUserSession),
        typeof(RemoveUserSession),
        typeof(InstallSystemPackage),
        typeof(UninstallSystemPackage)
    ];

    /// <summary>
    /// Logger instance
    /// </summary>
    private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Connection to the IPC client served by this processor
    /// </summary>
    public Connection Connection { get; }

    /// <summary>
    /// Constructor of the command interpreter
    /// </summary>
    /// <param name="conn">Connection instance</param>
    /// <param name="initMessage">Initialization message from the client</param>
    public Command(Connection conn, ClientInitMessage initMessage)
    {
        Connection = conn;

        _logger.Debug("Command processor added for IPC#{0}", conn.Id);
    }

    /// <summary>
    /// Reads incoming command requests and processes them. See <see cref="DuetAPI.Commands"/> namespace for a list
    /// of supported commands. The actual implementations can be found in <see cref="Commands"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the worker</param>
    /// <returns>Asynchronous task</returns>
    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        do
        {
            DuetAPI.Commands.BaseCommand? command = null;
            try
            {
                // Read another command from the IPC connection
                command = await Connection.ReceiveCommandAsync(SupportedCommands, cancellationToken);
                Type commandType = command.GetType();

                // Make sure it is actually supported and permitted
                if (!SupportedCommands.Contains(commandType))
                {
                    throw new ArgumentException($"Invalid command {command.Command} (wrong mode?)");
                }
                Connection.CheckPermissions(commandType);

                // Execute it and send back the result
                object? result = await command.InvokeAsync(cancellationToken);
                await Connection.SendResponseAsync(result);

                // Shut down the socket if this was the last command
                if (cancellationToken.IsCancellationRequested)
                {
                    Connection.Close();
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
                            _logger.Error("IPC#{0}: Insufficient permissions to execute {1}", Connection.Id, command.Command);
                        }
                        else
                        {
                            _logger.Error(e, "IPC#{0}: Failed to execute {1}", Connection.Id, command.Command);
                        }
                    }
                    else
                    {
                        _logger.Error(e, "IPC#{0}: Failed to receive command", Connection.Id);
                    }
                }
                await Connection.SendExceptionAsync(e);
            }
        }
        while (!cancellationToken.IsCancellationRequested);
    }
}
