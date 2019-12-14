using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
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
        /// UNIX socket for inter-process communication
        /// </summary>
        private static Socket _unixSocket;

        /// <summary>
        /// Initialize the IPC subsystem and start listening for connections
        /// </summary>
        public static void Init()
        {
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
            _unixSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                UnixDomainSocketEndPoint endPoint = new UnixDomainSocketEndPoint(Settings.FullSocketPath);
                _unixSocket.Bind(endPoint);
                _unixSocket.Listen(Settings.Backlog);
            }
            catch
            {
                throw;
            }
        }

        /// <summary>
        /// Process incoming connections
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Run()
        {
            // Make sure to terminate the main socket when the application is being terminated
            Program.CancelSource.Token.Register(_unixSocket.Close, false);

            // Start accepting incoming connections
            List<Task> connectionTasks = new List<Task>();
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
                                connectionTasks.Remove(task);
                            }
                        }
                        connectionTasks.Add(connectionTask);
                    }
                }
                while (true);
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
            using Connection connection = new Connection(socket);
            try
            {
                // Send server-side init message to the client
                connection.Logger.Debug("Got new UNIX connection, checking mode...");
                await connection.Send(new ServerInitMessage { Id = connection.Id });

                // Read client-side init message and switch mode
                Base processor = await GetConnectionProcessor(connection);
                if (processor != null)
                {
                    // Send success message
                    await connection.SendResponse();

                    // Let the processor deal with the connection
                    await processor.Process();
                }
                else
                {
                    connection.Logger.Debug("Failed to find processor");
                }
            }
            catch (Exception e)
            {
                if (!(e is OperationCanceledException) && !(e is SocketException))
                {
                    // Log unexpected errors
                    connection.Logger.Error(e, "Terminating connection because an unexpected exception was thrown");
                }
            }
            finally
            {
                connection.Logger.Debug("Connection closed");

                // Unlock the machine model again in case the client application crashed
                if (LockManager.Connection == connection.Id)
                {
                    LockManager.UnlockMachineModel();
                }
            }
        }

        /// <summary>
        /// Attempt to retrieve a processor for the given connection
        /// </summary>
        /// <param name="conn">Connection to get a processor for</param>
        /// <returns>Instance of a base processor</returns>
        private static async Task<Base> GetConnectionProcessor(Connection conn)
        {
            try
            {
                JsonDocument response = await conn.ReceiveJson();
                ClientInitMessage initMessage = JsonSerializer.Deserialize<ClientInitMessage>(response.RootElement.GetRawText(), JsonHelper.DefaultJsonOptions);
                switch (initMessage.Mode)
                {
                    case ConnectionMode.Command:
                        initMessage = JsonSerializer.Deserialize<CommandInitMessage>(response.RootElement.GetRawText(), JsonHelper.DefaultJsonOptions);
                        return new Command(conn);

                    case ConnectionMode.Intercept:
                        initMessage = JsonSerializer.Deserialize<InterceptInitMessage>(response.RootElement.GetRawText(), JsonHelper.DefaultJsonOptions);
                        return new Interception(conn, initMessage);

                    case ConnectionMode.Subscribe:
                        initMessage = JsonSerializer.Deserialize<SubscribeInitMessage>(response.RootElement.GetRawText(), JsonHelper.DefaultJsonOptions);
                        return new Subscription(conn, initMessage);

                    default:
                        throw new ArgumentException("Invalid connection mode");
                }
            }
            catch (Exception e)
            {
                conn.Logger.Error(e, "Failed to get connection processor");
                await conn.SendResponse(e);
            }

            return null;
        }
    }
}