using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.IPC
{
    public static class Server
    {
        private static readonly Socket unixSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private static readonly ConcurrentDictionary<Socket, int> clientSockets = new ConcurrentDictionary<Socket, int>();

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
            // Keep accepting incoming connections
            do
            {
                Socket socket = await unixSocket.AcceptAsync();
                clientSockets.TryAdd(socket, 0);

                // Deal with them asynchronously
                Task connTask = ConnectionEstablished(socket);
            }
            while (!Program.CancelSource.IsCancellationRequested);
        }

        private static async Task ConnectionEstablished(Socket socket)
        {
            // Handle a new connection.
            // By default an IPC client is expected to transmit standard commands from the DuetAPI.Commands namespace.
            // If requested, an IPC client can change its mode of operation to either Interception or Subscription.
            Worker.Base worker = new Worker.Command(socket);
            try
            {
                Worker.Base newWorker = worker;
                do
                {
                    newWorker = await worker.Work();
                    if (newWorker != null && worker != newWorker)
                    {
                        worker = newWorker;
                    }
                }
                while (newWorker != null);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Connection error: {e.Message}");
            }
            
            // Remove this connection again
            clientSockets.TryRemove(socket, out int dummy);
            if (socket.Connected)
            {
                socket.Disconnect(true);
            }
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