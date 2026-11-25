using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;

namespace DuetAPIClient;

/// <summary>
/// Connection class for streaming codes to the control server as text lines and for receiving their replies as text.
/// The stream returned by <see cref="GetStream"/> carries both, and also the raw file content of an M559 or M560 upload.
/// </summary>
/// <seealso cref="ConnectionMode.CodeStream"/>
public sealed class CodeStreamConnection : BaseConnection
{
    /// <summary>
    /// Creates a new connection in intercepting mode
    /// </summary>
    public CodeStreamConnection() : base(ConnectionMode.CodeStream) { }

    /// <summary>
    /// Establish a connection to the given UNIX socket file
    /// </summary>
    /// <param name="socketPath">Optional path to the DCS UNIX socket file</param>
    /// <param name="bufferSize">Maximum number of codes to execute simultaneously</param>
    /// <param name="channel">Destination channel for incoming codes</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="IncompatibleVersionException">API level is incompatible</exception>
    /// <exception cref="IOException">Connection mode is unavailable</exception>
    /// <exception cref="SocketException">Init message could not be processed</exception>
    public void Connect(int bufferSize = Defaults.CodeBufferSize, CodeChannel channel = Defaults.InputChannel, string? socketPath = null)
    {
        BufferSize = bufferSize;
        Channel = channel;

        CodeStreamInitMessage initMessage = new() { BufferSize = bufferSize, Channel = channel };
        Connect(initMessage, socketPath);
    }

    /// <summary>
    /// Establish a connection to the given UNIX socket file asynchronously
    /// </summary>
    /// <param name="bufferSize">Maximum number of codes to execute simultaneously</param>
    /// <param name="channel">Destination channel for incoming codes</param>
    /// <param name="socketPath">Optional path to the DCS UNIX socket file</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="IncompatibleVersionException">API level is incompatible</exception>
    /// <exception cref="IOException">Connection mode is unavailable</exception>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Init message could not be processed</exception>
    public async Task ConnectAsync(int bufferSize = Defaults.CodeBufferSize, CodeChannel channel = Defaults.InputChannel, string? socketPath = null, CancellationToken cancellationToken = default)
    {
        BufferSize = bufferSize;
        Channel = channel;

        CodeStreamInitMessage initMessage = new() { BufferSize = bufferSize, Channel = channel };
        await ConnectAsync(initMessage, socketPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Maximum number of codes being executed simultaneously
    /// </summary>
    public int BufferSize { get; private set; }

    /// <summary>
    /// Destination channel for incoming codes and source channel for incoming messages
    /// </summary>
    public CodeChannel Channel { get; private set; }

    /// <summary>
    /// Get a network stream for input/output of codes
    /// </summary>
    /// <returns></returns>
    public NetworkStream GetStream() => new(_unixSocket);
}
