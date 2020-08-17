using DuetAPI.ObjectModel;
using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.AddHttpEndpoint"/> command
    /// </summary>
    public sealed class AddHttpEndpoint : DuetAPI.Commands.AddHttpEndpoint
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Add a new HTTP endpoint
        /// </summary>
        /// <returns>Reserved file path to a UNIX socket</returns>
        public override async Task<string> Execute()
        {
            // Check if the namespace is reserved
            if (Namespace == "file" || Namespace == "fileinfo" || Namespace == "directory")
            {
                throw new ArgumentException("Namespace is reserved");
            }

            // Check if the requested HTTP endpoint has already been registered. If yes, it may be reused
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                foreach (HttpEndpoint endpoint in Model.Provider.Get.HttpEndpoints)
                {
                    if (endpoint.EndpointType == EndpointType && endpoint.Namespace == Namespace && endpoint.Path == Path)
                    {
                        if (IsUnixSocketAlive(endpoint.UnixSocket))
                        {
                            throw new InvalidOperationException("Requested HTTP endpoint is already registered and active");
                        }
                        return endpoint.UnixSocket;
                    }
                }
            }

            // Create a UNIX socket file like /var/run/dsf/mynamespace/myaction-GET.sock
            string socketPath = System.IO.Path.Combine(Settings.SocketDirectory, Namespace, $"{Path}-{EndpointType}.sock");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(socketPath));
            // NB: In case plugins are running as a separate user, the file has to be created here and the permissions have to be updated

            using (await Model.Provider.AccessReadWriteAsync())
            {
                HttpEndpoint endpoint = new HttpEndpoint();
                Model.Provider.Get.HttpEndpoints.Add(endpoint);

                endpoint.EndpointType = EndpointType;
                endpoint.Namespace = Namespace;
                endpoint.Path = Path;
                endpoint.UnixSocket = socketPath;
            }

            _logger.Debug("Registered new HTTP endpoint {0} machine/{1}/{2} via {3}", EndpointType, Namespace, Path, socketPath);
            return socketPath;
        }

        /// <summary>
        /// Check if the given UNIX socket file is accepting connections
        /// </summary>
        /// <param name="filename">Path to the UNIX socket file</param>
        /// <returns>True if the socket is still active</returns>
        private bool IsUnixSocketAlive(string filename)
        {
            try
            {
                using Socket testSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                testSocket.Connect(new UnixDomainSocketEndPoint(filename));
                testSocket.Close();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
