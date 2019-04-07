using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using DuetControlServer.IPC.Processors;
using Newtonsoft.Json.Linq;

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
        /// Start accepting incoming connections.
        /// This represents the lifecycle of this class
        /// </summary>
        /// <returns></returns>
        public static async Task AcceptConnections()
        {
            // Keep accepting incoming connections.
            // Each connection is assigned a unique ID
            do
            {
                Socket socket = await unixSocket.AcceptAsync();
                
                int id = Interlocked.Increment(ref LastConnectionID);
                ConnectionEstablished(socket, id);
            }
            while (!Program.CancelSource.IsCancellationRequested);
        }

        private static async void ConnectionEstablished(Socket socket, int id)
        {
            // Register it first
            clientSockets.TryAdd(socket, id);
            
            // Deal with the connection
            using (Connection conn = new Connection(socket, id))
            {
                // Send server-side init message to the client
                await conn.SendResponse(new ServerInitMessage { Id = id });
                
                // Read client-side init message and switch mode
                try
                {
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
                    // We get here as well when the connection has been terminated (SocketException -> Broken pipe)
                    if (!(e is SocketException))
                    {
                        Console.WriteLine($"Connection error: {e}");
                    }
                }
            }
            
            // Remove this connection again
            clientSockets.TryRemove(socket, out id);
            if (socket.Connected)
            {
                socket.Disconnect(true);
            }
        }

        private class DeserializableInitMessage : ClientInitMessage { }

        private static async Task<Base> GetConnectionProcessor(Connection conn)
        {
            try
            {
                JObject response = await conn.ReceiveJson();
                ClientInitMessage initMessage = response.ToObject<ClientInitMessage>();
                switch (initMessage.Mode)
                {
                    case ConnectionMode.Command:
                        initMessage = response.ToObject<CommandInitMessage>();
                        return new Command(conn, initMessage);
                    
                    case ConnectionMode.Intercept:
                        initMessage = response.ToObject<InterceptInitMessage>();
                        return new Interception(conn, initMessage);
                    
                    case ConnectionMode.Subscribe:
                        initMessage = response.ToObject<SubscribeInitMessage>();
                        return new Subscription(conn, initMessage);
                    
                    default:
                        throw new ArgumentException("Invalid connection mode");
                }
            }
            catch (Exception e)
            {
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