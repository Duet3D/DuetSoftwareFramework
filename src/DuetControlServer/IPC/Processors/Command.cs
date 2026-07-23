using System;
using System.Buffers;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Connection.InitMessages;
using DuetControlServer.Commands;
using DuetControlServer.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        typeof(QueryObjectModel),
        typeof(PatchObjectModel),
        typeof(SetUpdateStatus),
        typeof(SetWifiCountry),
        typeof(SyncObjectModel),
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
    private readonly ILogger<Command> _logger;

    /// <summary>
    /// Application settings
    /// </summary>
    private readonly Settings _settings;

    /// <summary>
    /// Connection to the IPC client served by this processor
    /// </summary>
    public Connection Connection { get; }

    /// <summary>
    /// Constructor of the command interpreter
    /// </summary>
    /// <param name="conn">Connection instance</param>
    /// <param name="initMessage">Initialization message from the client</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settings">Application settings</param>
    public Command(Connection conn, ClientInitMessage initMessage, ILogger<Command> logger, IOptions<Settings> settings)
    {
        Connection = conn;
        _logger = logger;
        _settings = settings.Value;

        _logger.LogDebug("Command processor added for IPC#{Id}", conn.Id);
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
                if (command is IRawJsonCommand rawJsonCommand)
                {
                    using PooledBufferWriter responseBuffer = new(_settings.IpcJsonBufferSize);
                    responseBuffer.Write(Connection.SuccessResponseStart);
                    await rawJsonCommand.ExecuteRawJsonAsync(responseBuffer, cancellationToken);
                    responseBuffer.Write(Connection.SuccessResponseEnd);
                    await Connection.SendRawDataAsync(responseBuffer.WrittenMemory);
                }
                else
                {
                    object? result = await command.InvokeAsync(cancellationToken);
                    await Connection.SendResponseAsync(result);
                }

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
                            _logger.LogError("IPC#{Id}: Cannot execute {Command}: {Message}", Connection.Id, command.Command, e.Message);
                        }
                        else
                        {
                            _logger.LogError(e, "IPC#{Id}: Failed to execute {Command}", Connection.Id, command.Command);
                        }
                    }
                    else
                    {
                        _logger.LogError(e, "IPC#{Id}: Failed to receive command", Connection.Id);
                    }
                }
                await Connection.SendExceptionAsync(e);
            }
        }
        while (!cancellationToken.IsCancellationRequested);
    }
}
