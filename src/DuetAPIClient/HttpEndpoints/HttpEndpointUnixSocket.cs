using DuetAPI.ObjectModel;
using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DuetAPIClient
{
    /// <summary>
    /// Class for dealing with custom HTTP endpoints
    /// </summary>
    public sealed class HttpEndpointUnixSocket : IDisposable
    {
        #region Libc access
        private const string LibcLibrary = "libc";

        [DllImport(LibcLibrary, SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int chmod(string pathname, ushort mode);

        const uint GroupRwMode = 0x30;   // 000110000, mode 060

        [StructLayout(LayoutKind.Sequential)]
        private struct statbuf
        {
            public uint DeviceID;
            public uint InodeNumber;
            public uint Mode;
            public uint HardLinks;
            public uint UserID;
            public uint GroupID;
            public uint SpecialDeviceID;
            public ulong Size;
            public ulong BlockSize;
            public uint Blocks;
            public long TimeLastAccess;
            public long TimeLastModification;
            public long TimeLastStatusChange;
        }

        private const int STATVER = 1;

        [DllImport(LibcLibrary, EntryPoint = "__xstat", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int stat(int vers, string pathname, ref statbuf statbuf);
        #endregion

        /// <summary>
        /// Default number of pending connections
        /// </summary>
        public const int DefaultBacklog = 4;

        /// <summary>
        /// Type of this HTTP endpoint
        /// </summary>
        public HttpEndpointType EndpointType { get; }

        /// <summary>
        /// Namespace of this HTTP endpoint
        /// </summary>
        public string Namespace { get; }

        /// <summary>
        /// Path of this HTTP endpoint
        /// </summary>
        public string EndpointPath { get; }

        /// <summary>
        /// Path to the UNIX socket file
        /// </summary>
        public string SocketPath { get; }

        /// <summary>
        /// Actual UNIX socket instance
        /// </summary>
        private readonly Socket _unixSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        /// <summary>
        /// Open a new UNIX socket on the given file path
        /// </summary>
        /// <param name="endpointType">Type of this HTTP endpoint</param>
        /// <param name="ns">Namespace of this HTTP endpoint</param>
        /// <param name="endpointPath">Path of this HTTP endpoint</param>
        /// <param name="socketPath">Path to the UNIX socket file</param>
        /// <param name="backlog">Number of simultaneously pending connections</param>
        /// <exception cref="IOException">Socket could not be opened</exception>
        public HttpEndpointUnixSocket(HttpEndpointType endpointType, string ns, string endpointPath, string socketPath, int backlog = DefaultBacklog)
        {
            // Set up information about this HTTP endpoint
            EndpointType = endpointType;
            Namespace = ns;
            EndpointPath = endpointPath;
            SocketPath = socketPath;

            // Clean up socket path again in case of unclean exit
            File.Delete(socketPath);

            // Create a new UNIX socket and start listening
            try
            {
                // Create a new UNIX socket
                UnixDomainSocketEndPoint endPoint = new UnixDomainSocketEndPoint(socketPath);
                _unixSocket.Bind(endPoint);
                _unixSocket.Listen(backlog);

                // Allow the group to write to it
                statbuf buf = new statbuf();
                if (stat(STATVER, socketPath, ref buf) == 0)
                {
                    chmod(socketPath, (ushort)(buf.Mode | GroupRwMode));
                }

                // Start listening
                AcceptConnections();
            }
            catch
            {
                _unixSocket.Close();
                throw;
            }
        }


        /// <summary>
        /// Indicates if this instance has been disposed
        /// </summary>
        private bool disposed;

        /// <summary>
        /// Disposes all used resources
        /// </summary>
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            _unixSocket.Dispose();
            File.Delete(SocketPath);

            disposed = true;
        }

        /// <summary>
        /// Delegate of the event that is called when a new request is being received
        /// </summary>
        /// <param name="unixSocket">UNIX socket in charge of the endpoint</param>
        /// <param name="requestConnection">Connection representing a request from a client</param>
        public delegate void EndpointRequestReceived(HttpEndpointUnixSocket unixSocket, HttpEndpointConnection requestConnection);

        /// <summary>
        /// Event that is triggered whenever a new HTTP request is received
        /// </summary>
        public event EndpointRequestReceived OnEndpointRequestReceived;

        /// <summary>
        /// Accept incoming UNIX socket connections (HTTP/WebSocket requests)
        /// </summary>
        private async void AcceptConnections()
        {
            try
            {
                do
                {
                    Socket socket = await _unixSocket.AcceptAsync();
                    HttpEndpointConnection connection = new HttpEndpointConnection(socket, EndpointType == HttpEndpointType.WebSocket);

                    if (OnEndpointRequestReceived != null)
                    {
                        // Invoke the event handler and forward the wrapped connection for dealing with a single endpoint connection
                        // Note that the event delegate is responsible for disposing the connection!
                        OnEndpointRequestReceived.Invoke(this, connection);
                    }
                    else
                    {
                        // Cannot do anything with this connection. Send an HTTP 500 response and close the connection
                        await connection.SendResponse(500, "No event handler registered");
                        connection.Dispose();
                    }
                }
                while (true);
            }
            catch (SocketException)
            {
                // may happen if an endpoint handler deals with a terminated connection synchronously
            }
        }
    }
}
