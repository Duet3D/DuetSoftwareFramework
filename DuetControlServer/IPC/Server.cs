using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Connection;
using Newtonsoft.Json.Linq;

namespace DuetControlServer.IPC
{
    public static class Server
    {
        private static readonly Socket unixSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        
        private static readonly ConcurrentDictionary<Socket, int> clientSockets = new ConcurrentDictionary<Socket, int>();
        private static int LastConnectionID;

        public static void CreateSocket()
        {
            // Clean up socket path again in case of unclean exit
            System.IO.File.Delete(Settings.SocketPath);
            
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
                    Processors.Base processor = await GetConnectionProcessor(conn);
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

        private static async Task<Processors.Base> GetConnectionProcessor(Connection conn)
        {
            try
            {
                JObject response = await conn.ReceiveJson();
                ClientInitMessage initMessage = response.ToObject<ClientInitMessage>();
                switch (initMessage.Type)
                {
                    case ConnectionType.Command:
                        initMessage = response.ToObject<CommandInitMessage>();
                        return new Processors.Command(conn, initMessage);
                    
                    case ConnectionType.Intercept:
                        initMessage = response.ToObject<InterceptInitMessage>();
                        return new Processors.Interception(conn, initMessage);
                    
                    case ConnectionType.Subscribe:
                        initMessage = response.ToObject<SubscribeInitMessage>();
                        return new Processors.Subscription(conn, initMessage);
                    
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
        
        public static void Shutdown()
        {
            // Disconnect every client
            foreach (Socket socket in clientSockets.Keys)
            {
                socket.Close();
            }

            // Clean up again
            unixSocket.Close();
            System.IO.File.Delete(Settings.SocketPath);
        }
    }
}