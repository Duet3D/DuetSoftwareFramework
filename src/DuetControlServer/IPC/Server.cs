using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
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
        private static readonly Socket unixSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        
        private static readonly ConcurrentDictionary<Socket, int> clientSockets = new ConcurrentDictionary<Socket, int>();
        private static int LastConnectionID;

        /// <summary>
        /// Create the UNIX socket for IPC
        /// </summary>
        public static void CreateSocket()
        {
            // Clean up socket path again in case of unclean exit
            File.Delete(Settings.SocketPath);
            
            // Create a new UNIX socket and start listening
            try
            {
                UnixDomainSocketEndPoint endPoint = new UnixDomainSocketEndPoint(Settings.SocketPath);
                unixSocket.Bind(endPoint);
                unixSocket.Listen(Settings.Backlog);
            }
            catch
            {
                unixSocket.Close();
                throw;
            }
        }

        /// <summary>
        /// Accept incoming connections as long as this program is runnning
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task AcceptConnections()
        {
            do
            {
                try
                {
                    Socket socket = await unixSocket.AcceptAsync();
                    ConnectionEstablished(socket);
                }
                catch (SocketException)
                {
                    throw new OperationCanceledException(Program.CancelSource.Token);
                }
            }
            while (!Program.CancelSource.IsCancellationRequested);
        }

        private static async void ConnectionEstablished(Socket socket)
        {
            // Register the new connection first
            int id = Interlocked.Increment(ref LastConnectionID);
            clientSockets.TryAdd(socket, id);
            
            // Wrap it
            Connection conn = new Connection(socket, id);
            try
            {
                // Send server-side init message to the client
                await conn.Send(new ServerInitMessage { Id = id });

                // Read client-side init message and switch mode
                Base processor = await GetConnectionProcessor(conn);
                if (processor != null)
                {
                    // Send success message
                    await conn.SendResponse();

                    // Let the processor deal with the connection
                    await processor.Process();
                }
            }
            catch (Exception e)
            {
                // We get here as well when the connection has been terminated (IOException, SocketException -> Broken pipe)
                if (!(e is OperationCanceledException))
                {
                    Console.WriteLine($"[warn] Connection error: {e}");
                }
            }
            finally
            {
                // Remove this connection again
                clientSockets.TryRemove(socket, out _);
                if (socket.Connected)
                {
                    socket.Disconnect(true);
                }
            }
        }

        private class DeserializableInitMessage : ClientInitMessage { }

        private static async Task<Base> GetConnectionProcessor(Connection conn)
        {
            try
            {
                JsonDocument response = await conn.ReceiveJson();
                if (response == null)
                {
                    return null;
                }

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
                Console.WriteLine($"[err] Failed to get connection processor: {e}");
                await conn.SendResponse(e);
            }

            return null;
        }

        /// <summary>
        /// Close every connection and clean up the UNIX socket
        /// </summary>
        public static void Shutdown()
        {
            // Disconnect every client
            foreach (Socket socket in clientSockets.Keys)
            {
                socket.Close();
            }

            // Clean up again
            unixSocket.Close();
            File.Delete(Settings.SocketPath);
        }
    }
}