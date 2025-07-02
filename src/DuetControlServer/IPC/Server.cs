using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using DuetControlServer.Commands;
using DuetControlServer.IPC.Processors;
using DuetControlServer.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DuetControlServer.IPC;

/// <summary>
/// Static class that holds main functionality for inter-process communication
/// </summary>
/// <param name="commandFactory">Factory to create commands</param>
/// <param name="processorFactory">Factory to create connection processors</param>
/// <param name="lockManager">Lock manager to handle read/write locks</param>
/// <param name="model">Object model</param>
/// <param name="settings">Settings</param>
[DiagnosticsPriority(-8)]
public class Server(CommandFactory commandFactory, ProcessorFactory processorFactory, LockManager lockManager, Model.ObjectModel model, IOptions<Settings> settings) : BackgroundService, IDiagnostics
{
    /// <summary>
    /// Minimum supported protocol version number
    /// </summary>
    /// <seealso cref="Defaults.ProtocolVersion"/>
    public const int MinimumProtocolVersion = 7;

    /// <summary>
    /// Logger instance
    /// </summary>
    private readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// UNIX socket for inter-process communication
    /// </summary>
    private readonly Socket _unixSocket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

    /// <summary>
    /// Initialize the IPC subsystem and start listening for connections
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (settings.Value.UpdateOnly)
        {
            // Don't do anything if only the firmware is supposed to be updated
            return base.StartAsync(cancellationToken);
        }

        // Make sure the parent directory exists but the socket file does not
        if (File.Exists(settings.Value.FullSocketPath))
        {
            File.Delete(settings.Value.FullSocketPath);
        }
        else
        {
            Directory.CreateDirectory(settings.Value.SocketDirectory);
        }

        // Create a new UNIX socket and start listening
        UnixDomainSocketEndPoint endPoint = new(settings.Value.FullSocketPath);
        _unixSocket.Bind(endPoint);
        _unixSocket.Listen(settings.Value.Backlog);

        // Start main service
        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Process incoming connections
    /// </summary>
    /// <returns>Asynchronous task</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Don't listen for incoming connections if only the firmware is being updated
        if (settings.Value.UpdateOnly)
        {
            await Task.Delay(-1, stoppingToken);
            return;
        }

        // Start accepting incoming connections
        List<Task> connectionTasks = [];
        try
        {
            do
            {
                Socket socket = await _unixSocket.AcceptAsync(stoppingToken);
                Task connectionTask = Task.Run(async () => await ProcessConnectionAsync(socket, stoppingToken), stoppingToken);
                lock (connectionTasks)
                {
                    for (int i = connectionTasks.Count - 1; i >= 0; i--)
                    {
                        Task task = connectionTasks[i];
                        if (task.IsCompleted)
                        {
                            connectionTasks.RemoveAt(i);
                        }
                    }
                    connectionTasks.Add(connectionTask);
                }
            }
            while (!stoppingToken.IsCancellationRequested);
        }
        catch (SocketException)
        {
            // expected when the program terminates
        }

        // Wait for pending connections to go
        await Task.WhenAll(connectionTasks);
    }

    /// <summary>
    /// Stop the IPC server and close the UNIX socket
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        // Close the UNIX socket
        _unixSocket.Close();

        // Remove the UNIX socket file again
        File.Delete(settings.Value.FullSocketPath);

        // Done
        return base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Function that is called when a new connection has been established
    /// </summary>
    /// <param name="socket">Socket of the new connection</param>
    /// <returns>Asynchronous task</returns>
    private async Task ProcessConnectionAsync(Socket socket, CancellationToken cancellationToken)
    {
        using Connection connection = new(socket, commandFactory);
        try
        {
            // Check if this connection is permitted
            _logger.Debug("Got new connection IPC#{0}, checking permissions...", connection.Id);
            if (await connection.AssignPermissions(model))
            {
                // Send server-side init message to the client
                await connection.SendInitMessage(new ServerInitMessage { Id = connection.Id });

                // Read client-side init message and switch mode
                IProcessor? processor = await GetConnectionProcessorAsync(connection, cancellationToken);
                if (processor is not null)
                {
                    try
                    {
                        // Send success message
                        await connection.SendResponse();

                        // Let the processor deal with the connection
                        await processor.ProcessAsync(cancellationToken);
                    }
                    finally
                    {
                        // Dispose of the processor if necessary
                        if (processor is IDisposable disposableProcessor)
                        {
                            disposableProcessor.Dispose();
                        }
                    }
                }
                else
                {
                    _logger.Debug("IPC#{0}: Failed to find processor", connection.Id);
                }
            }
            else
            {
                _logger.Warn("IPC#{0}: Terminating connection due to insufficient permissions", connection.Id);
                await connection.SendException(new UnauthorizedAccessException("Insufficient permissions"));
            }
        }
        catch (Exception e)
        {
            if (e is not OperationCanceledException && e is not SocketException)
            {
                // Log unexpected errors
                _logger.Error(e, "IPC#{0}: Terminating connection due to unexpected exception", connection.Id);
            }
        }
        finally
        {
            _logger.Debug("IPC#{0}: Connection closed", connection.Id);

            // Unlock the machine model again in case the client application crashed
            await lockManager.UnlockMachineModel(connection, cancellationToken);
        }
    }

    /// <summary>
    /// Attempt to retrieve a processor for the given connection asynchronously
    /// </summary>
    /// <param name="conn">Connection to get a processor for</param>
    /// <returns>Instance of a base processor</returns>
    private async Task<IProcessor?> GetConnectionProcessorAsync(Connection conn, CancellationToken cancellationToken)
    {
        try
        {
            // Read the init message from the client
            ClientInitMessage initMessage = await conn.ReceiveInitMessageAsync(cancellationToken);
            conn.ApiVersion = initMessage.Version;

            // Check the version number
            if (initMessage.Version < MinimumProtocolVersion || initMessage.Version > Defaults.ProtocolVersion)
            {
                string message = $"Incompatible protocol version (got {initMessage.Version}, need {MinimumProtocolVersion} to {Defaults.ProtocolVersion})";
                _logger.Warn("IPC#{0}: {1}", conn.Id, message);
                await conn.SendResponse(new IncompatibleVersionException(message));
                return null;
            }
            else if (initMessage.Version != Defaults.ProtocolVersion)
            {
                _logger.Warn("IPC#{0}: Client with outdated protocol version connected (got {1}, want {2})", conn.Id, initMessage.Version, Defaults.ProtocolVersion);
            }

            // Check the requested mode
            switch (initMessage.Mode)
            {
                case ConnectionMode.Command:
                    if (!conn.CheckCommandPermissions(Command.SupportedCommands))
                    {
                        throw new UnauthorizedAccessException("Insufficient permissions");
                    }
                    return processorFactory.Create<Command>(conn, initMessage);

                case ConnectionMode.Intercept:
                    if (!conn.CheckCommandPermissions(CodeInterception.SupportedCommands))
                    {
                        throw new UnauthorizedAccessException("Insufficient permissions");
                    }
                    return processorFactory.Create<CodeInterception>(conn, initMessage);

                case ConnectionMode.Subscribe:
                    if (!conn.CheckCommandPermissions(ModelSubscription.SupportedCommands))
                    {
                        throw new UnauthorizedAccessException("Insufficient permissions");
                    }
                    return processorFactory.Create<ModelSubscription>(conn, initMessage);

                case ConnectionMode.CodeStream:
                    if (!conn.CheckCommandPermissions(CodeStream.SupportedCommands))
                    {
                        throw new UnauthorizedAccessException("Insufficient permissions");
                    }
                    return processorFactory.Create<CodeStream>(conn, initMessage);

                case ConnectionMode.PluginService:
                    return processorFactory.Create<PluginService>(conn, initMessage);

                default:
                    throw new ArgumentException("Invalid connection mode");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException and not SocketException)
        {
            _logger.Error(e, "IPC#{0}: Failed to assign connection processor", conn.Id);
            await conn.SendResponse(e);
        }

        return null;
    }

    /// <summary>
    /// Print diagnostics
    /// </summary>
    /// <param name="builder">String builder to write to</param>
    public void PrintDiagnostics(StringBuilder builder)
    {
        CodeInterception.PrintDiagnostics(builder);
    }
}