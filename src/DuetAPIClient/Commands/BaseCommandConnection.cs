using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;

namespace DuetAPIClient;

/// <summary>
/// Base connection class for sending commands to the control server
/// </summary>
/// <seealso cref="ConnectionMode.Command"/>
/// <remarks>
/// Protected constructor for derived modes that can issue regular commands
/// </remarks>
/// <param name="mode">Connection type</param>
public abstract class BaseCommandConnection(ConnectionMode mode) : BaseConnection(mode)
{
    /// <summary>
    /// Add a new third-party HTTP endpoint in the format /machine/{ns}/{path}
    /// </summary>
    /// <param name="endpointType">HTTP request type</param>
    /// <param name="ns">Namespace of the plugin</param>
    /// <param name="path">Endpoint path</param>
    /// <param name="backlog">Number of simultaneously pending connections</param>
    /// <param name="isUploadRequest">Whether this is an upload request</param>
    /// <returns>Wrapper around the UNIX socket for accepting HTTP endpoint requests</returns>
    /// <exception cref="ArgumentException">Endpoint namespace is reserved</exception>
    /// <exception cref="InvalidOperationException">Endpoint is already in use</exception>
    /// <exception cref="IOException">UNIX socket could not be opened</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.RegisterHttpEndpoints"/>
    [UnsupportedOSPlatform("windows")]
    public HttpEndpointUnixSocket AddHttpEndpoint(HttpEndpointType endpointType, string ns, string path, bool isUploadRequest = false, int backlog = HttpEndpointUnixSocket.DefaultBacklog)
    {
        string socketPath = PerformCommand<string>(new AddHttpEndpoint { EndpointType = endpointType, Namespace = ns, Path = path, IsUploadRequest = isUploadRequest });
        return new HttpEndpointUnixSocket(endpointType, ns, path, socketPath, backlog);
    }

    /// <summary>
    /// Add a new third-party HTTP endpoint in the format /machine/{ns}/{path}
    /// </summary>
    /// <param name="endpointType">HTTP request type</param>
    /// <param name="ns">Namespace of the plugin</param>
    /// <param name="path">Endpoint path</param>
    /// <param name="backlog">Number of simultaneously pending connections</param>
    /// <param name="isUploadRequest">Whether this is an upload request</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Wrapper around the UNIX socket for accepting HTTP endpoint requests</returns>
    /// <exception cref="ArgumentException">Endpoint namespace is reserved</exception>
    /// <exception cref="InvalidOperationException">Endpoint is already in use</exception>
    /// <exception cref="IOException">UNIX socket could not be opened</exception>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.RegisterHttpEndpoints"/>
    [UnsupportedOSPlatform("windows")]
    public async Task<HttpEndpointUnixSocket> AddHttpEndpointAsync(HttpEndpointType endpointType, string ns, string path, bool isUploadRequest = false, int backlog = HttpEndpointUnixSocket.DefaultBacklog, CancellationToken cancellationToken = default)
    {
        string socketPath = await PerformCommandAsync<string>(new AddHttpEndpoint { EndpointType = endpointType, Namespace = ns, Path = path, IsUploadRequest = isUploadRequest }, cancellationToken).ConfigureAwait(false);
        return new HttpEndpointUnixSocket(endpointType, ns, path, socketPath, backlog);
    }

    /// <summary>
    /// Add a new user session
    /// </summary>
    /// <param name="access">Access level of this session</param>
    /// <param name="type">Type of this session</param>
    /// <param name="origin">Origin of the user session (e.g. IP address or PID)</param>
    /// <returns>New session ID</returns>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ManageUserSessions"/>
    public int AddUserSession(AccessLevel access, SessionType type, string? origin = null)
    {
#if NET6_0_OR_GREATER
        origin ??= Environment.ProcessId.ToString();
#else
        origin ??= Process.GetCurrentProcess().Id.ToString();
#endif
        return PerformCommand<int>(new AddUserSession { AccessLevel = access, SessionType = type, Origin = origin });
    }

    /// <summary>
    /// Add a new user session asynchronously
    /// </summary>
    /// <param name="access">Access level of this session</param>
    /// <param name="type">Type of this session</param>
    /// <param name="origin">Origin of the user session (e.g. IP address or PID)</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>New session ID</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ManageUserSessions"/>
    public async Task<int> AddUserSessionAsync(AccessLevel access, SessionType type, string? origin = null, CancellationToken cancellationToken = default)
    {
#if NET6_0_OR_GREATER
        origin ??= Environment.ProcessId.ToString();
#else
        origin ??= Process.GetCurrentProcess().Id.ToString();
#endif
        return await PerformCommandAsync<int>(new AddUserSession { AccessLevel = access, SessionType = type, Origin = origin }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Check the given password (see M551)
    /// </summary>
    /// <param name="password">Password to check</param>
    /// <returns>True if the requested password is correct</returns>
    /// <exception cref="InvalidOperationException">Requested code channel is disabled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    public bool CheckPassword(string password) => PerformCommand<bool>(new CheckPassword { Password = password });

    /// <summary>
    /// Check the given password asynchronously (see M551)
    /// </summary>
    /// <param name="password">Password to check</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>True if the requested password is correct</returns>
    /// <exception cref="InvalidOperationException">Requested code channel is disabled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    public async Task<bool> CheckPasswordAsync(string password, CancellationToken cancellationToken = default)
    {
        return await PerformCommandAsync<bool>(new CheckPassword { Password = password }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Evaluate an arbitrary expression
    /// </summary>
    /// <param name="channel">Context of the evaluation</param>
    /// <param name="expression">Expression to evaluate</param>
    /// <returns>Evaluation result</returns>
    /// <exception cref="InvalidOperationException">Requested code channel is disabled</exception>
    /// <exception cref="JsonException">Expected and returned data type do not match</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    public JsonElement EvaluateExpression(string expression, CodeChannel channel = CodeChannel.SBC)
    {
        return PerformCommand<JsonElement>(new EvaluateExpression { Channel = channel, Expression = expression });
    }

    /// <summary>
    /// Evaluate an arbitrary expression asynchronously
    /// </summary>
    /// <param name="channel">Context of the evaluation</param>
    /// <param name="expression">Expression to evaluate</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Evaluation result</returns>
    /// <exception cref="InvalidOperationException">Requested code channel is disabled</exception>
    /// <exception cref="JsonException">Expected and returned data type do not match</exception>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    public async Task<JsonElement> EvaluateExpressionAsync(string expression, CodeChannel channel = CodeChannel.SBC, CancellationToken cancellationToken = default)
    {
        return await PerformCommandAsync<JsonElement>(new EvaluateExpression { Channel = channel, Expression = expression }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Wait for all pending codes of the given channel to finish
    /// </summary>
    /// <param name="channel">Code channel to wait for</param>
    /// <returns>True if all pending codes could be flushed</returns>
    /// <exception cref="InvalidOperationException">Requested code channel is disabled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    public bool Flush(CodeChannel channel) => PerformCommand<bool>(new Flush { Channel = channel });

    /// <summary>
    /// Wait for all pending codes of the given channel to finish asynchronously
    /// </summary>
    /// <param name="channel">Code channel to wait for</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>True if all pending codes could be flushed</returns>
    /// <exception cref="InvalidOperationException">Requested code channel is disabled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    public async Task<bool> FlushAsync(CodeChannel channel, CancellationToken cancellationToken = default)
    {
        return await PerformCommandAsync<bool>(new Flush { Channel = channel }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parse a G-code file and returns file information about it
    /// </summary>
    /// <param name="fileName">The file to parse</param>
    /// <returns>Information about the parsed file</returns>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="GetFileInfo.GetFileInfo"/>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    /// <seealso cref="SbcPermissions.FileSystemAccess"/>
    /// <seealso cref="SbcPermissions.ReadGCodes"/>
    public GCodeFileInfo GetFileInfo(string fileName) => PerformCommand<GCodeFileInfo>(new GetFileInfo { FileName = fileName });

    /// <summary>
    /// Parse a G-code file and returns file information about it asynchronously
    /// </summary>
    /// <param name="fileName">The file to parse</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Information about the parsed file</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="GetFileInfo.GetFileInfo"/>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    /// <seealso cref="SbcPermissions.FileSystemAccess"/>
    /// <seealso cref="SbcPermissions.ReadGCodes"/>
    public async Task<GCodeFileInfo> GetFileInfoAsync(string fileName, CancellationToken cancellationToken = default)
    {
        return await PerformCommandAsync<GCodeFileInfo>(new GetFileInfo { FileName = fileName }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parse a G-code file and returns file information about it
    /// </summary>
    /// <param name="fileName">The file to parse</param>
    /// <param name="readThumbnailContent">Whether thumbnail content shall be returned</param>
    /// <returns>Information about the parsed file</returns>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="GetFileInfo.GetFileInfo"/>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    /// <seealso cref="SbcPermissions.FileSystemAccess"/>
    /// <seealso cref="SbcPermissions.ReadGCodes"/>
    public GCodeFileInfo GetFileInfo(string fileName, bool readThumbnailContent)
    {
        return PerformCommand<GCodeFileInfo>(new GetFileInfo { FileName = fileName, ReadThumbnailContent = readThumbnailContent });
    }

    /// <summary>
    /// Parse a G-code file and returns file information about it asynchronously
    /// </summary>
    /// <param name="fileName">The file to parse</param>
    /// <param name="readThumbnailContent">Whether thumbnail content shall be returned</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Information about the parsed file</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="GetFileInfo.GetFileInfo"/>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    /// <seealso cref="SbcPermissions.FileSystemAccess"/>
    /// <seealso cref="SbcPermissions.ReadGCodes"/>
    public async Task<GCodeFileInfo> GetFileInfoAsync(string fileName, bool readThumbnailContent, CancellationToken cancellationToken = default)
    {
        return await PerformCommandAsync<GCodeFileInfo>(new GetFileInfo { FileName = fileName, ReadThumbnailContent = readThumbnailContent }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieve the full object model of the machine.
    /// In subscription mode this is the first command that has to be called once a connection has been established
    /// </summary>
    /// <returns>The current machine model</returns>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ObjectModelRead"/>
    /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
    public ObjectModel GetObjectModel() => PerformCommand<ObjectModel>(new GetObjectModel());

    /// <summary>
    /// Retrieve the full object model of the machine asynchronously.
    /// In subscription mode this is the first command that has to be called once a connection has been established
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>The current machine model</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ObjectModelRead"/>
    /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
    public async Task<ObjectModel> GetObjectModelAsync(CancellationToken cancellationToken = default)
    {
        return await PerformCommandAsync<ObjectModel>(new GetObjectModel(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Optimized method to directly query the machine model JSON
    /// </summary>
    /// <returns>Machine model JSON</returns>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ObjectModelRead"/>
    /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
    public string GetSerializedObjectModel()
    {
        JsonElement model = PerformCommand<JsonElement>(new GetObjectModel());
        return model.GetRawText();
    }

    /// <summary>
    /// Optimized method to directly query the machine model JSON
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Machine model JSON</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ObjectModelRead"/>
    /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
    public async Task<string> GetSerializedObjectModelAsync(CancellationToken cancellationToken = default)
    {
        JsonElement model = await PerformCommandAsync<JsonElement>(new GetObjectModel(), cancellationToken).ConfigureAwait(false);
        return model.GetRawText();
    }

    /// <summary>
    /// Install or upgrade a plugin
    /// </summary>
    /// <param name="packageFile">Absolute file path to the plugin ZIP bundle</param>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ManagePlugins"/>
    public void InstallPlugin(string packageFile) => PerformCommand(new InstallPlugin { PluginFile = packageFile });

    /// <summary>
    /// Install or upgrade a plugin asynchronously
    /// </summary>
    /// <param name="packageFile">Absolute file path to the plugin ZIP bundle</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ManagePlugins"/>
    public async Task InstallPluginAsync(string packageFile, CancellationToken cancellationToken = default)
    {
        await PerformCommandAsync(new InstallPlugin { PluginFile = packageFile }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Install or upgrade a system package
    /// </summary>
    /// <param name="packageFile">Absolute file path to the package file</param>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.SuperUser"/>
    public void InstallSystemPackage(string packageFile) => PerformCommand(new InstallSystemPackage { PackageFile = packageFile });

    /// <summary>
    /// Install or upgrade a system package asynchronously
    /// </summary>
    /// <param name="packageFile">Absolute file path to the package file</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.SuperUser"/>
    public async Task InstallSystemPackageAsync(string packageFile, CancellationToken cancellationToken = default)
    {
        await PerformCommandAsync(new InstallSystemPackage { PackageFile = packageFile }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Invalidate all pending codes and files on the given channel
    /// </summary>
    /// <remarks>
    /// This does NOT cancel the current code being executed by RRF!
    /// </remarks>
    /// <param name="channel">Code channel where everything is supposed to be invalidated</param>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.SuperUser"/>
    public void InvalidateChannel(CodeChannel channel) => PerformCommand(new InvalidateChannel { Channel = channel });

    /// <summary>
    /// Invalidate all pending codes and files on the given channel
    /// </summary>
    /// <remarks>
    /// This does NOT cancel the current code being executed by RRF!
    /// </remarks>
    /// <param name="channel">Code channel where everything is supposed to be invalidated</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.SuperUser"/>
    public async Task InvalidateChannelAsync(CodeChannel channel, CancellationToken cancellationToken = default)
    {
        await PerformCommandAsync(new InvalidateChannel { Channel = channel }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Internal class representing an object model lock
    /// </summary>
    /// <param name="connection">Connection that acquired the lock</param>
    public sealed class ObjectModelLock(BaseCommandConnection connection) : IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// Dispose the lock again
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public void Dispose()
        {
            if (connection.IsConnected)
            {
                connection.PerformCommand(new UnlockObjectModel());
            }
        }

        /// <summary>
        /// Dispose the lock again asynchronously
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public async ValueTask DisposeAsync()
        {
            if (connection.IsConnected)
            {
                await connection.PerformCommandAsync(new UnlockObjectModel(), default).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Lock the machine model for read/write access
    /// </summary>
    /// <returns>Asynchronous object model lock</returns>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
    public ObjectModelLock LockObjectModel()
    {
        PerformCommand(new LockObjectModel());
        return new ObjectModelLock(this);
    }

    /// <summary>
    /// Lock the machine model for read/write access asynchronously
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous object model lock</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
    public async Task<ObjectModelLock> LockObjectModelAsync(CancellationToken cancellationToken = default)
    {
        await PerformCommandAsync(new LockObjectModel(), cancellationToken).ConfigureAwait(false);
        return new ObjectModelLock(this);
    }

    /// <summary>
    /// Notify the control server that a plugin has been started
    /// </summary>
    /// <param name="plugin">Plugin ID (only needed if running as root)</param>
    /// <exception cref="SocketException">Command could not be processed</exception>
    public void NotifyPluginStarted(string? plugin = null)
    {
        PerformCommand(new NotifyPluginStarted { Plugin = plugin });
    }

    /// <summary>
    /// Notify the control server that a plugin has been started
    /// </summary>
    /// <param name="plugin">Plugin ID (only needed if running as root)</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    public async Task NotifyPluginStartedAsync(string? plugin = null, CancellationToken cancellationToken = default)
    {
        await PerformCommandAsync(new NotifyPluginStarted { Plugin = plugin }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Apply a full patch to the object model. Use with care!
    /// </summary>
    /// <param name="key">Key to update</param>
    /// <param name="patch">Patch to apply</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
    public void PatchObjectModel(string key, JsonElement patch) => PerformCommand(new PatchObjectModel() { Key = key, Patch = patch });

    /// <summary>
    /// Apply a full patch to the object model asynchronously. Use with care!
    /// </summary>
    /// <param name="key">Key to update</param>
    /// <param name="patch">Patch to apply</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
    public async Task PatchObjectModelAsync(string key, JsonElement patch, CancellationToken cancellationToken = default)
    {
        await PerformCommandAsync(new PatchObjectModel() { Key = key, Patch = patch }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Execute an arbitrary pre-parsed code
    /// </summary>
    /// <param name="code">The code to execute</param>
    /// <returns>Result of the given code</returns>
    /// <exception cref="InvalidOperationException">Requested code channel is disabled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <remarks>Cancelling the read operation does not cancel the code execution</remarks>
    /// <seealso cref="Code"/>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    public Message PerformCode(Code code) => PerformCommand<Message>(code);

    /// <summary>
    /// Execute an arbitrary pre-parsed code asynchronously
    /// </summary>
    /// <param name="code">The code to execute</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Result of the given code</returns>
    /// <exception cref="InvalidOperationException">Requested code channel is disabled</exception>
    /// <exception cref="OperationCanceledException">Code or operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <remarks>Cancelling the read operation does not cancel the code execution</remarks>
    /// <seealso cref="Code"/>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    public async Task<Message> PerformCodeAsync(Code code, CancellationToken cancellationToken = default)
    {
        return await PerformCommandAsync<Message>(code, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Execute an arbitrary G/M/T-code in text form and return the result as a string
    /// </summary>
    /// <param name="code">The code to execute</param>
    /// <param name="channel">Optional destination channel of this code</param>
    /// <returns>Result of the given code converted to a string</returns>
    /// <exception cref="InvalidOperationException">Requested code channel is disabled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <remarks>Cancelling the read operation does not cancel the code execution</remarks>
    /// <seealso cref="SimpleCode"/>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    public string PerformSimpleCode(string code, CodeChannel channel = Defaults.InputChannel)
    {
        return PerformCommand<string>(new SimpleCode { Code = code, Channel = channel });
    }

    /// <summary>
    /// Execute an arbitrary G/M/T-code in text form and return the result as a string asynchronously
    /// </summary>
    /// <param name="code">The code to execute</param>
    /// <param name="channel">Optional destination channel of this code</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Result of the given code converted to a string</returns>
    /// <exception cref="InvalidOperationException">Requested code channel is disabled</exception>
    /// <exception cref="OperationCanceledException">Code or operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <remarks>Cancelling the read operation does not cancel the code execution</remarks>
    /// <seealso cref="SimpleCode"/>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    public async Task<string> PerformSimpleCodeAsync(string code, CodeChannel channel = Defaults.InputChannel, CancellationToken cancellationToken = default)
    {
        return await PerformCommandAsync<string>(new SimpleCode { Code = code, Channel = channel }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Execute an arbitrary G/M/T-code in text form and return the result as a string
    /// </summary>
    /// <param name="code">The code to execute</param>
    /// <param name="channel">Optional destination channel of this code</param>
    /// <param name="executeAsynchronously">Execute this code asynchronously in the background</param>
    /// <returns>Result of the given code converted to a string</returns>
    /// <exception cref="InvalidOperationException">Requested code channel is disabled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <remarks>Cancelling the read operation does not cancel the code execution</remarks>
    /// <seealso cref="SimpleCode"/>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    public string PerformSimpleCode(string code, CodeChannel channel, bool executeAsynchronously)
    {
        return PerformCommand<string>(new SimpleCode { Code = code, Channel = channel, ExecuteAsynchronously = executeAsynchronously });
    }

    /// <summary>
    /// Execute an arbitrary G/M/T-code in text form and return the result as a string asynchronously
    /// </summary>
    /// <param name="code">The code to execute</param>
    /// <param name="channel">Optional destination channel of this code</param>
    /// <param name="executeAsynchronously">Execute this code asynchronously in the background</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Result of the given code converted to a string</returns>
    /// <exception cref="InvalidOperationException">Requested code channel is disabled</exception>
    /// <exception cref="OperationCanceledException">Code or operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <remarks>Cancelling the read operation does not cancel the code execution</remarks>
    /// <seealso cref="SimpleCode"/>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    public async Task<string> PerformSimpleCodeAsync(string code, CodeChannel channel, bool executeAsynchronously, CancellationToken cancellationToken = default)
    {
        return await PerformCommandAsync<string>(new SimpleCode { Code = code, Channel = channel, ExecuteAsynchronously = executeAsynchronously }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reload a plugin manifest
    /// </summary>
    /// <param name="plugin">Identifier of the plugin</param>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ManagePlugins"/>
    public void ReloadPlugin(string plugin) => PerformCommand(new ReloadPlugin { Plugin = plugin });

    /// <summary>
    /// Reload a plugin manifest
    /// </summary>
    /// <param name="plugin">Identifier of the plugin</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ManagePlugins"/>
    public async Task ReloadPluginAsync(string plugin, CancellationToken cancellationToken = default)
    {
        await PerformCommandAsync(new ReloadPlugin { Plugin = plugin }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Remove an existing HTTP endpoint
    /// </summary>
    /// <param name="endpointType">Type of the endpoint to remove</param>
    /// <param name="ns">Namespace of the endpoint to remove</param>
    /// <param name="path">Endpoint to remove</param>
    /// <returns>True if the endpoint could be removed</returns>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.RegisterHttpEndpoints"/>
    public bool RemoveHttpEndpoint(HttpEndpointType endpointType, string ns, string path)
    {
        return PerformCommand<bool>(new RemoveHttpEndpoint { EndpointType = endpointType, Namespace = ns, Path = path });
    }

    /// <summary>
    /// Remove an existing HTTP endpoint
    /// </summary>
    /// <param name="endpointType">Type of the endpoint to remove</param>
    /// <param name="ns">Namespace of the endpoint to remove</param>
    /// <param name="path">Endpoint to remove</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>True if the endpoint could be removed</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.RegisterHttpEndpoints"/>
    public async Task<bool> RemoveHttpEndpointAsync(HttpEndpointType endpointType, string ns, string path, CancellationToken cancellationToken = default)
    {
        return await PerformCommandAsync<bool>(new RemoveHttpEndpoint { EndpointType = endpointType, Namespace = ns, Path = path }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Remove an existing user session
    /// </summary>
    /// <param name="id">Identifier of the session</param>
    /// <returns>True if the session could be removed</returns>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ManageUserSessions"/>
    public bool RemoveUserSession(int id) => PerformCommand<bool>(new RemoveUserSession { Id = id });

    /// <summary>
    /// Remove an existing user session asynchronously
    /// </summary>
    /// <param name="id">Identifier of the session</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>True if the session could be removed</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ManageUserSessions"/>
    public async Task<bool> RemoveUserSessionAsync(int id, CancellationToken cancellationToken = default)
    {
        return await PerformCommandAsync<bool>(new RemoveUserSession { Id = id }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve a RepRapFirmware-style file path to a real file path
    /// </summary>
    /// <param name="path">File path to resolve</param>
    /// <returns>Resolved file path</returns>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    /// <seealso cref="SbcPermissions.FileSystemAccess"/>
    public string ResolvePath(string path) => PerformCommand<string>(new ResolvePath { Path = path });

    /// <summary>
    /// Resolve a RepRapFirmware-style file path to a real file path asynchronously
    /// </summary>
    /// <param name="path">File path to resolve</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Resolved file path</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    /// <seealso cref="SbcPermissions.FileSystemAccess"/>
    public async Task<string> ResolvePathAsync(string path, CancellationToken cancellationToken = default)
    {
        return await PerformCommandAsync<string>(new ResolvePath { Path = path }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve a RepRapFirmware-style file path to a real file path
    /// </summary>
    /// <param name="path">File path to resolve</param>
    /// <param name="baseDirectory">Base directory to resolve the path relative to</param>
    /// <returns>Resolved file path</returns>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    /// <seealso cref="SbcPermissions.FileSystemAccess"/>
    public string ResolvePath(string path, FileDirectory baseDirectory)
    {
        return PerformCommand<string>(new ResolvePath { Path = path, BaseDirectory = baseDirectory });
    }

    /// <summary>
    /// Resolve a RepRapFirmware-style file path to a real file path asynchronously
    /// </summary>
    /// <param name="path">File path to resolve</param>
    /// <param name="baseDirectory">Base directory to resolve the path relative to</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Resolved file path</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    /// <seealso cref="SbcPermissions.FileSystemAccess"/>
    public async Task<string> ResolvePathAsync(string path, FileDirectory baseDirectory, CancellationToken cancellationToken = default)
    {
        return await PerformCommandAsync<string>(new ResolvePath { Path = path, BaseDirectory = baseDirectory }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Set a given property to a certain value. Make sure to lock the object model before calling this
    /// </summary>
    /// <param name="path">Path to the property</param>
    /// <param name="value">New value as string</param>
    /// <returns>True if the property could be updated</returns>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
    public bool SetObjectModel(string path, string value)
    {
        return PerformCommand<bool>(new SetObjectModel { PropertyPath = path, Value = value });
    }

    /// <summary>
    /// Set a given property to a certain value. Make sure to lock the object model before calling this
    /// </summary>
    /// <param name="path">Path to the property</param>
    /// <param name="value">New value as string</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>True if the property could be updated</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
    public async Task<bool> SetObjectModelAsync(string path, string value, CancellationToken cancellationToken = default)
    {
        return await PerformCommandAsync<bool>(new SetObjectModel { PropertyPath = path, Value = value }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Set custom plugin data in the object model
    /// </summary>
    /// <param name="key">Key to set</param>
    /// <param name="value">Value to set</param>
    /// <param name="plugin">Identifier of the plugin to update (optional)</param>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <exception cref="UnauthorizedAccessException">Insufficient permissions to modify other plugin data</exception>
    /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
    /// <seealso cref="SbcPermissions.ManagePlugins"/>
    public void SetPluginData(string key, JsonElement value, string? plugin = null)
    {
        PerformCommand(new SetPluginData { Plugin = plugin, Key = key, Value = value });
    }

    /// <summary>
    /// Set custom plugin data in the object model asynchronously
    /// </summary>
    /// <param name="key">Key to set</param>
    /// <param name="value">Value to set</param>
    /// <param name="plugin">Identifier of the plugin to update (optional)</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <exception cref="UnauthorizedAccessException">Insufficient permissions to modify other plugin data</exception>
    /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
    /// <seealso cref="SbcPermissions.ManagePlugins"/>
    public async Task SetPluginDataAsync(string key, JsonElement value, string? plugin = null, CancellationToken cancellationToken = default)
    {
        await PerformCommandAsync(new SetPluginData { Plugin = plugin, Key = key, Value = value }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Override the current machine status if a software update is in progress
    /// </summary>
    /// <param name="isUpdating">If the machine status is supposed to be overrridden</param>
    /// <remarks>
    /// The object model must not be locked when this is called
    /// </remarks>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <exception cref="UnauthorizedAccessException">Insufficient permissions to modify other plugin data</exception>
    /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
    public void SetUpdateStatus(bool isUpdating) => PerformCommand(new SetUpdateStatus { Updating = isUpdating });

    /// <summary>
    /// Override the current machine status asynchronously if a software update is in progress
    /// </summary>
    /// <param name="isUpdating">If the machine status is supposed to be overrridden</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <remarks>
    /// The object model must not be locked when this is called
    /// </remarks>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <exception cref="UnauthorizedAccessException">Insufficient permissions to modify other plugin data</exception>
    /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
    public async Task SetUpdateStatusAsync(bool isUpdating, CancellationToken cancellationToken = default)
    {
        await PerformCommandAsync(new SetUpdateStatus { Updating = isUpdating }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Start a plugin
    /// </summary>
    /// <param name="plugin">Identifier of the plugin</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ManagePlugins"/>
    public void StartPlugin(string plugin) => PerformCommand(new StartPlugin { Plugin = plugin });

    /// <summary>
    /// Start a plugin asynchronously
    /// </summary>
    /// <param name="plugin">Identifier of the plugin</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ManagePlugins"/>
    public async Task StartPluginAsync(string plugin, CancellationToken cancellationToken = default)
    {
        await PerformCommandAsync(new StartPlugin { Plugin = plugin }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stop a plugin
    /// </summary>
    /// <param name="plugin">Identifier of the plugin</param>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ManagePlugins"/>
    public void StopPlugin(string plugin) => PerformCommand(new StopPlugin { Plugin = plugin });

    /// <summary>
    /// Stop a plugin asynchronously
    /// </summary>
    /// <param name="plugin">Identifier of the plugin</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ManagePlugins"/>
    public async Task StopPluginAsync(string plugin, CancellationToken cancellationToken = default)
    {
        await PerformCommandAsync(new StopPlugin { Plugin = plugin }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Wait for the full object model to be updated from RepRapFirmware
    /// </summary>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    /// <seealso cref="SbcPermissions.ObjectModelRead"/>
    /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
    public void SyncObjectModel() => PerformCommand(new SyncObjectModel());

    /// <summary>
    /// Wait asynchronously for the full object model to be updated from RepRapFirmware
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    /// <seealso cref="SbcPermissions.ObjectModelRead"/>
    /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
    public async Task SyncObjectModelAsync(CancellationToken cancellationToken = default)
    {
        await PerformCommandAsync(new SyncObjectModel(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Uninstall a plugin
    /// </summary>
    /// <param name="plugin">Identifier of the plugin</param>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ManagePlugins"/>
    public void UninstallPlugin(string plugin) => PerformCommand(new UninstallPlugin { Plugin = plugin });

    /// <summary>
    /// Uninstall a plugin asynchronously
    /// </summary>
    /// <param name="plugin">Identifier of the plugin</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.ManagePlugins"/>
    public async Task UninstallPluginAsync(string plugin, CancellationToken cancellationToken = default)
    {
        await PerformCommandAsync(new UninstallPlugin { Plugin = plugin }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Uninstall a system package
    /// </summary>
    /// <param name="package">Identifier of the package</param>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.SuperUser"/>
    public void UninstallSystemPackage(string package) => PerformCommand(new UninstallSystemPackage { Package = package });

    /// <summary>
    /// Uninstall a system package asynchronously
    /// </summary>
    /// <param name="package">Identifier of the package</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.SuperUser"/>
    public async Task UninstallSystemPackageAsync(string package, CancellationToken cancellationToken = default)
    {
        await PerformCommandAsync(new UninstallSystemPackage { Package = package }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Write an arbitrary generic message
    /// </summary>
    /// <param name="type">Message type</param>
    /// <param name="message">Message content</param>
    /// <param name="outputMessage">Whether to output the message</param>
    /// <param name="logLevel">Target log level or null to determine log level from the message type</param>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
    public void WriteMessage(MessageType type, string message, bool outputMessage = true, LogLevel? logLevel = null)
    {
        PerformCommand(new WriteMessage { Type = type, Content = message, OutputMessage = outputMessage, LogLevel = logLevel });
    }

    /// <summary>
    /// Write an arbitrary generic message asynchronously
    /// </summary>
    /// <param name="type">Message type</param>
    /// <param name="message">Message content</param>
    /// <param name="outputMessage">Whether to output the message</param>
    /// <param name="logLevel">Target log level or null to determine log level from the message type</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
    public async Task WriteMessageAsync(MessageType type, string message, bool outputMessage = true, LogLevel? logLevel = null, CancellationToken cancellationToken = default)
    {
        await PerformCommandAsync(new WriteMessage { Type = type, Content = message, OutputMessage = outputMessage, LogLevel = logLevel }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Write an arbitrary generic message
    /// </summary>
    /// <param name="message">Message</param>
    /// <param name="outputMessage">Whether to output the message</param>
    /// <param name="logLevel">Target log level</param>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
    public void WriteMessage(Message message, bool outputMessage = true, LogLevel logLevel = LogLevel.Off)
    {
        PerformCommand(new WriteMessage { Type = message.Type, Content = message.Content, OutputMessage = outputMessage, LogLevel = logLevel });
    }

    /// <summary>
    /// Write an arbitrary generic message
    /// </summary>
    /// <param name="message">Message</param>
    /// <param name="outputMessage">Whether to output the message</param>
    /// <param name="logLevel">Target log level</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
    public async Task WriteMessageAsync(Message message, bool outputMessage = true, LogLevel logLevel = LogLevel.Off, CancellationToken cancellationToken = default)
    {
        await PerformCommandAsync(new WriteMessage { Type = message.Type, Content = message.Content, OutputMessage = outputMessage, LogLevel = logLevel }, cancellationToken).ConfigureAwait(false);
    }
}
