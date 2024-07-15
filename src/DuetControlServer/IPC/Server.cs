using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using DuetAPI.Utility;
using DuetControlServer.IPC.Processors;

namespace DuetControlServer.IPC
{
    /// <summary>
    /// Static class that holds main functionality for inter-process communication
    /// </summary>
    public static class Server
    {
        /// <summary>
        /// Minimum supported protocol version number
        /// </summary>
        /// <seealso cref="Defaults.ProtocolVersion"/>
        public const int MinimumProtocolVersion = 7;

        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// UNIX socket for inter-process communication
        /// </summary>
        private static readonly Socket _unixSocket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        /// <summary>
        /// Initialize the IPC subsystem and start listening for connections
        /// </summary>
        public static void Init()
        {
            if (Settings.UpdateOnly)
            {
                // Don't do anything if only the firmware is supposed to be updated
                return;
            }

            // Make sure the parent directory exists but the socket file does not
            if (File.Exists(Settings.FullSocketPath))
            {
                File.Delete(Settings.FullSocketPath);
            }
            else
            {
                Directory.CreateDirectory(Settings.SocketDirectory);
            }

            // Create a new UNIX socket and start listening
            UnixDomainSocketEndPoint endPoint = new(Settings.FullSocketPath);
            _unixSocket.Bind(endPoint);
            _unixSocket.Listen(Settings.Backlog);
        }

        /// <summary>
        /// Process incoming connections
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Run()
        {
            // Don't listen for incoming connections if only the firmware is being updated
            if (Settings.UpdateOnly)
            {
                await Task.Delay(-1, Program.CancellationToken);
                return;
            }

            // Make sure to terminate the main socket when the application is being terminated
            Program.CancellationToken.Register(_unixSocket.Close, false);

            // Start accepting incoming connections
            List<Task> connectionTasks = [];
            try
            {
                do
                {
                    Socket socket = await _unixSocket.AcceptAsync();
                    Task connectionTask = Task.Run(async () => await ProcessConnection(socket));
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
                while (!Program.CancellationToken.IsCancellationRequested);
            }
            catch (SocketException)
            {
                // expected when the program terminates
            }

            // Wait for pending connections to go
            await Task.WhenAll(connectionTasks);

            // Remove the UNIX socket file again
            File.Delete(Settings.FullSocketPath);
        }

        /// <summary>
        /// Function that is called when a new connection has been established
        /// </summary>
        /// <param name="socket">Socket of the new connection</param>
        /// <returns>Asynchronous task</returns>
        private static async Task ProcessConnection(Socket socket)
        {
            using Connection connection = new(socket);
            try
            {
                // Check if this connection is permitted
                _logger.Debug("Got new connection IPC#{0}, checking permissions...", connection.Id);
                if (await connection.AssignPermissions())
                {
                    // Send server-side init message to the client
                    await connection.Send(new ServerInitMessage { Id = connection.Id });

                    // Read client-side init message and switch mode
                    Base? processor = await GetConnectionProcessor(connection);
                    if (processor is not null)
                    {
                        // Send success message
                        await connection.SendResponse();

                        // Let the processor deal with the connection
                        await processor.Process();
                    }
                    else
                    {
                        _logger.Debug("IPC#{0}: Failed to find processor", connection.Id);
                    }
                }
                else
                {
                    _logger.Warn("IPC#{0}: Terminating connection due to insufficient permissions", connection.Id);
                    await connection.Send(new UnauthorizedAccessException("Insufficient permissions"));
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
                await LockManager.UnlockMachineModel(connection);
            }
        }

        /// <summary>
        /// Attempt to retrieve a processor for the given connection
        /// </summary>
        /// <param name="conn">Connection to get a processor for</param>
        /// <returns>Instance of a base processor</returns>
        private static async Task<Base?> GetConnectionProcessor(Connection conn)
        {
            try
            {
                // Read the init message from the client
                string response = await conn.ReceivePlainJson();
                ClientInitMessage initMessage = JsonSerializer.Deserialize<ClientInitMessage>(response, JsonHelper.DefaultJsonOptions)!;
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
                        initMessage = JsonSerializer.Deserialize<CommandInitMessage>(response, JsonHelper.DefaultJsonOptions)!;
                        return new Command(conn);

                    case ConnectionMode.Intercept:
                        if (!conn.CheckCommandPermissions(CodeInterception.SupportedCommands))
                        {
                            throw new UnauthorizedAccessException("Insufficient permissions");
                        }
                        initMessage = JsonSerializer.Deserialize<InterceptInitMessage>(response, JsonHelper.DefaultJsonOptions)!;
                        return new CodeInterception(conn, initMessage);

                    case ConnectionMode.Subscribe:
                        if (!conn.CheckCommandPermissions(ModelSubscription.SupportedCommands))
                        {
                            throw new UnauthorizedAccessException("Insufficient permissions");
                        }
                        initMessage = JsonSerializer.Deserialize<SubscribeInitMessage>(response, JsonHelper.DefaultJsonOptions)!;
                        return new ModelSubscription(conn, initMessage);

                    case ConnectionMode.CodeStream:
                        if (!conn.CheckCommandPermissions(CodeStream.SupportedCommands))
                        {
                            throw new UnauthorizedAccessException("Insufficient permissions");
                        }
                        initMessage = JsonSerializer.Deserialize<CodeStreamInitMessage>(response, JsonHelper.DefaultJsonOptions)!;
                        return new CodeStream(conn, initMessage);

                    case ConnectionMode.PluginService:
                        initMessage = JsonSerializer.Deserialize<PluginServiceInitMessage>(response, JsonHelper.DefaultJsonOptions)!;
                        return new PluginService(conn);

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
    }
}