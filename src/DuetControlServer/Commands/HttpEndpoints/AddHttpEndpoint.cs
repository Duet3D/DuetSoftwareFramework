using DuetAPI.ObjectModel;
using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.AddHttpEndpoint"/> command
/// </summary>
/// <param name="model">Object model</param>
/// <param name="settings">Settings</param>
public sealed class AddHttpEndpoint(Model.ObjectModel model, IOptions<Settings> settings) : DuetAPI.Commands.AddHttpEndpoint
{
    /// <summary>
    /// Logger instance
    /// </summary>
    private readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Add a new HTTP endpoint
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Reserved file path to a UNIX socket</returns>
    public override async Task<string> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // Check if the namespace is reserved
        if (Namespace == "file" || Namespace == "fileinfo" || Namespace == "directory")
        {
            throw new ArgumentException("Namespace is reserved");
        }

        // Check if the requested HTTP endpoint has already been registered. If yes, it may be reused
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            foreach (HttpEndpoint endpoint in model.SBC!.DSF.HttpEndpoints)
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

        // Create a UNIX socket file like /run/dsf/mynamespace/myaction-GET.sock
        string socketPath = System.IO.Path.Combine(settings.Value.SocketDirectory, Namespace, $"{Path}-{EndpointType}.sock");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(socketPath)!);

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            HttpEndpoint endpoint = new();
            model.SBC!.DSF.HttpEndpoints.Add(endpoint);

            endpoint.EndpointType = EndpointType;
            endpoint.Namespace = Namespace;
            endpoint.Path = Path;
            endpoint.IsUploadRequest = IsUploadRequest;
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
    private static bool IsUnixSocketAlive(string filename)
    {
        try
        {
            using Socket testSocket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
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
