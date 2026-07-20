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
using DuetAPI.Utility;
using DuetControlServer.Commands;
using DuetControlServer.IPC.Processors;
using DuetControlServer.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DuetControlServer.IPC;

/// <summary>
/// Class that holds main functionality for inter-process communication
/// </summary>
/// <param name="commandFactory">Factory to create commands</param>
/// <param name="processorFactory">Factory to create connection processors</param>
/// <param name="lockManager">Lock manager to handle read/write locks</param>
/// <param name="model">Object model</param>
/// <param name="lifetime">Host application lifetime</param>
/// <param name="logger">Logger instance</param>
/// <param name="settings">Settings</param>
[DiagnosticsPriority(-2)]
public sealed class Server(CommandFactory commandFactory,
    ProcessorFactory processorFactory,
    LockManager lockManager,
    Model.ObjectModel model,
    IHostApplicationLifetime lifetime,
    ILogger<Server> logger,
    IOptions<Settings> settings) : BackgroundService, IDiagnostics
{
    /// <summary>
    /// Minimum supported protocol version number
    /// </summary>
    /// <seealso cref="Defaults.ProtocolVersion"/>
    public const int MinimumProtocolVersion = 7;

    /// <summary>
    /// UNIX socket for inter-process communication
    /// </summary>
    private readonly Socket _unixSocket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

    /// <summary>
    /// Initialize the IPC subsystem and start listening for connections
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
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
        logger.LogInformation("IPC socket created at {File}", settings.Value.FullSocketPath);

        // Start main service
        await base.StartAsync(cancellationToken);
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
            try
            {
                await Task.Delay(-1, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
            return;
        }

        // Start accepting incoming connections
        List<Task> connectionTasks = [];
        try
        {
            do
            {
                Socket socket = await _unixSocket.AcceptAsync(stoppingToken);
                Task connectionTask = Task.Run(() => ProcessConnectionAsync(socket, stoppingToken), stoppingToken);
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
        catch (Exception e) when (e is OperationCanceledException or SocketException)
        {
            // expected on shutdown
        }

        // Wait for pending connections to go
        await Task.WhenAll(connectionTasks).WaitAsync(lifetime.ApplicationStopped);
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
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    private async Task ProcessConnectionAsync(Socket socket, CancellationToken cancellationToken)
    {
        using Connection connection = new(socket, commandFactory, logger);
        try
        {
            // Check if this connection is permitted
            logger.LogDebug("Got new connection IPC#{Id}, checking permissions...", connection.Id);
            if (await connection.AssignPermissionsAsync(model))
            {
                // Send server-side init message to the client
                await connection.SendInitMessageAsync(new ServerInitMessage { Id = connection.Id });

                // Read client-side init message and switch mode
                IProcessor? processor = await GetConnectionProcessorAsync(connection, cancellationToken);
                if (processor is not null)
                {
                    try
                    {
                        // Send success message
                        await connection.SendResponseAsync();

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
                    logger.LogDebug("IPC#{Id}: Failed to find processor", connection.Id);
                }
            }
            else
            {
                logger.LogWarning("IPC#{Id}: Terminating connection due to insufficient permissions", connection.Id);
                await connection.SendExceptionAsync(new UnauthorizedAccessException("Insufficient permissions"));
            }
        }
        catch (Exception e)
        {
            if (e is not OperationCanceledException && e is not SocketException)
            {
                // Log unexpected errors
                logger.LogError(e, "IPC#{Id}: Terminating connection due to unexpected exception", connection.Id);
            }
        }
        finally
        {
            logger.LogDebug("IPC#{Id}: Connection closed", connection.Id);

            // Unlock the machine model again in case the client application crashed
            lockManager.UnlockMachineModel(connection);
        }
    }

    /// <summary>
    /// Attempt to retrieve a processor for the given connection asynchronously
    /// </summary>
    /// <param name="conn">Connection to get a processor for</param>
    /// <param name="cancellationToken">Cancellation token</param>
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
                logger.LogWarning("IPC#{Id}: {Message}", conn.Id, message);
                await conn.SendExceptionAsync(new IncompatibleVersionException(message));
                return null;
            }
            else if (initMessage.Version != Defaults.ProtocolVersion)
            {
                logger.LogWarning("IPC#{Id}: Client with outdated protocol version connected (got {Version}, want {WantedVersion})", conn.Id, initMessage.Version, Defaults.ProtocolVersion);
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
                    if (!conn.Permissions.HasFlag(SbcPermissions.ServicePlugins))
                    {
                        throw new UnauthorizedAccessException("Insufficient permissions");
                    }
                    return processorFactory.Create<PluginService>(conn, initMessage);

                default:
                    throw new ArgumentException("Invalid connection mode");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException and not SocketException)
        {
            logger.LogError(e, "IPC#{Id}: Failed to assign connection processor", conn.Id);
            await conn.SendExceptionAsync(e);
        }

        return null;
    }

    /// <summary>
    /// Print diagnostics
    /// </summary>
    /// <param name="builder">String builder to write to</param>
    public void PrintDiagnostics(StringBuilder builder)
    {
        CodeInterception.Diagnostics(builder);
    }
}
