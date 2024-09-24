using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;

namespace DuetAPIClient
{
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
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Wrapper around the UNIX socket for accepting HTTP endpoint requests</returns>
        /// <exception cref="ArgumentException">Endpoint namespace is reserved</exception>
        /// <exception cref="InvalidOperationException">Endpoint is already in use</exception>
        /// <exception cref="IOException">UNIX socket could not be opened</exception>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="SbcPermissions.RegisterHttpEndpoints"/>
        public async Task<HttpEndpointUnixSocket> AddHttpEndpoint(HttpEndpointType endpointType, string ns, string path, bool isUploadRequest = false, int backlog = HttpEndpointUnixSocket.DefaultBacklog, CancellationToken cancellationToken = default)
        {
            string socketPath = await PerformCommand<string>(new AddHttpEndpoint { EndpointType = endpointType, Namespace = ns, Path = path, IsUploadRequest = isUploadRequest }, cancellationToken);
            return new HttpEndpointUnixSocket(endpointType, ns, path, socketPath, backlog);
        }

        /// <summary>
        /// Add a new user session
        /// </summary>
        /// <param name="access">Access level of this session</param>
        /// <param name="type">Type of this session</param>
        /// <param name="origin">Origin of the user session (e.g. IP address or PID)</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>New session ID</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="SbcPermissions.ManageUserSessions"/>
        public Task<int> AddUserSession(AccessLevel access, SessionType type, string? origin = null, CancellationToken cancellationToken = default)
        {
#if NET6_0_OR_GREATER
            origin ??= Environment.ProcessId.ToString();
#else
            origin ??= Process.GetCurrentProcess().Id.ToString();
#endif
            return PerformCommand<int>(new AddUserSession { AccessLevel = access, SessionType = type, Origin = origin }, cancellationToken);
        }

        /// <summary>
        /// Check the given password (see M551)
        /// </summary>
        /// <param name="password">Password to check</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if the requested password is correct</returns>
        /// <exception cref="InvalidOperationException">Requested code channel is disabled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="SbcPermissions.CommandExecution"/>
        public Task<bool> CheckPassword(string password, CancellationToken cancellationToken = default)
        {
            return PerformCommand<bool>(new CheckPassword { Password = password }, cancellationToken);
        }

        /// <summary>
        /// Evaluate an arbitrary expression
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
        public Task<JsonElement> EvaluateExpression(string expression, CodeChannel channel = CodeChannel.SBC, CancellationToken cancellationToken = default)
        {
            return PerformCommand<JsonElement>(new EvaluateExpression { Channel = channel, Expression = expression }, cancellationToken);
        }

        /// <summary>
        /// Wait for all pending codes of the given channel to finish
        /// </summary>
        /// <param name="channel">Code channel to wait for</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if all pending codes could be flushed</returns>
        /// <exception cref="InvalidOperationException">Requested code channel is disabled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="SbcPermissions.CommandExecution"/>
        public Task<bool> Flush(CodeChannel channel, CancellationToken cancellationToken = default)
        {
            return PerformCommand<bool>(new Flush { Channel = channel }, cancellationToken);
        }

        /// <summary>
        /// Parse a G-code file and returns file information about it
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
        public Task<GCodeFileInfo> GetFileInfo(string fileName, CancellationToken cancellationToken = default)
        {
            return PerformCommand<GCodeFileInfo>(new GetFileInfo { FileName = fileName }, cancellationToken);
        }

        /// <summary>
        /// Parse a G-code file and returns file information about it
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
        public Task<GCodeFileInfo> GetFileInfo(string fileName, bool readThumbnailContent, CancellationToken cancellationToken = default)
        {
            return PerformCommand<GCodeFileInfo>(new GetFileInfo { FileName = fileName, ReadThumbnailContent = readThumbnailContent }, cancellationToken);
        }

        /// <summary>
        /// Retrieve the full object model of the machine.
        /// In subscription mode this is the first command that has to be called once a connection has been established
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>The current machine model</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        [Obsolete("Deprecated in favor of GetObjectModel")]
        public Task<ObjectModel> GetMachineModel(CancellationToken cancellationToken = default)
        {
            return PerformCommand<ObjectModel>(new GetObjectModel(), cancellationToken);
        }

        /// <summary>
        /// Retrieve the full object model of the machine.
        /// In subscription mode this is the first command that has to be called once a connection has been established
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>The current machine model</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="SbcPermissions.ObjectModelRead"/>
        /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
        public Task<ObjectModel> GetObjectModel(CancellationToken cancellationToken = default)
        {
            return PerformCommand<ObjectModel>(new GetObjectModel(), cancellationToken);
        }

        /// <summary>
        /// Optimized method to directly query the machine model UTF-8 JSON
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Machine model JSON</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        [Obsolete("Deprecated in favor of GetSerializedObjectModel")]
        public async ValueTask<MemoryStream> GetSerializedMachineModel(CancellationToken cancellationToken = default)
        {
            JsonElement model = await PerformCommand<JsonElement>(new GetObjectModel(), cancellationToken);
            MemoryStream stream = new();
            using Utf8JsonWriter writer = new(stream);
            model.WriteTo(writer);
            return stream;
        }

        /// <summary>
        /// Optimized method to directly query the machine model UTF-8 JSON
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Machine model JSON</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="SbcPermissions.ObjectModelRead"/>
        /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
        public async Task<string> GetSerializedObjectModel(CancellationToken cancellationToken = default)
        {
            JsonElement model = await PerformCommand<JsonElement>(new GetObjectModel(), cancellationToken);
            return model.GetRawText();
        }

        /// <summary>
        /// Install or upgrade a plugin
        /// </summary>
        /// <param name="packageFile">Absolute file path to the plugin ZIP bundle</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="SbcPermissions.ManagePlugins"/>
        public Task InstallPlugin(string packageFile, CancellationToken cancellationToken = default)
        {
            return PerformCommand(new InstallPlugin { PluginFile = packageFile }, cancellationToken);
        }

        /// <summary>
        /// Install or upgrade a system package
        /// </summary>
        /// <param name="packageFile">Absolute file path to the package file</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="SbcPermissions.SuperUser"/>
        public Task InstallSystemPackage(string packageFile, CancellationToken cancellationToken = default)
        {
            return PerformCommand(new InstallSystemPackage { PackageFile = packageFile }, cancellationToken);
        }

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
        public Task InvalidateChannel(CodeChannel channel, CancellationToken cancellationToken = default)
        {
            return PerformCommand(new InvalidateChannel { Channel = channel }, cancellationToken);
        }

        /// <summary>
        /// Internal class representing an object model lock
        /// </summary>
        /// <param name="connection">Connection that acquired the lock</param>
        private sealed class ObjectModelLock(BaseCommandConnection connection) : IAsyncDisposable
        {
            /// <summary>
            /// Dispose the lock again
            /// </summary>
            /// <returns>Asynchronous task</returns>
            public async ValueTask DisposeAsync()
            {
                if (connection.IsConnected)
                {
                    await connection.PerformCommand(new UnlockObjectModel(), default);
                }
            }
        }

        /// <summary>
        /// Lock the machine model for read/write access.
        /// It is MANDATORY to call <see cref="UnlockMachineModel"/> when write access has finished
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        [Obsolete("Deprecated in favor of LockObjectModel")]
        public Task LockMachineModel(CancellationToken cancellationToken = default)
        {
            return PerformCommand(new LockObjectModel(), cancellationToken);
        }

        /// <summary>
        /// Lock the machine model for read/write access.
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous object model lock</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
        public async Task<IAsyncDisposable> LockObjectModel(CancellationToken cancellationToken = default)
        {
            await PerformCommand(new LockObjectModel(), cancellationToken);
            return new ObjectModelLock(this);
        }

        /// <summary>
        /// Apply a full patch to the object model. Use with care!
        /// </summary>
        /// <param name="key">Key to update</param>
        /// <param name="patch">Patch to apply</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
        public async Task PatchObjectModel(string key, JsonElement patch, CancellationToken cancellationToken = default)
        {
            await PerformCommand(new PatchObjectModel() { Key = key, Patch = patch }, cancellationToken);
        }

        /// <summary>
        /// Execute an arbitrary pre-parsed code
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
        public Task<Message> PerformCode(Code code, CancellationToken cancellationToken = default)
        {
            return PerformCommand<Message>(code, cancellationToken);
        }

        /// <summary>
        /// Execute an arbitrary G/M/T-code in text form and return the result as a string
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
        public Task<string> PerformSimpleCode(string code, CodeChannel channel = Defaults.InputChannel, CancellationToken cancellationToken = default)
        {
            return PerformCommand<string>(new SimpleCode { Code = code, Channel = channel }, cancellationToken);
        }

        /// <summary>
        /// Execute an arbitrary G/M/T-code in text form and return the result as a string
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
        public Task<string> PerformSimpleCode(string code, CodeChannel channel, bool executeAsynchronously, CancellationToken cancellationToken = default)
        {
            return PerformCommand<string>(new SimpleCode { Code = code, Channel = channel, ExecuteAsynchronously = executeAsynchronously }, cancellationToken);
        }

        /// <summary>
        /// Reload a plugin manifest
        /// </summary>
        /// <param name="plugin">Identifier of the plugin</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="SbcPermissions.ManagePlugins"/>
        public Task ReloadPlugin(string plugin, CancellationToken cancellationToken = default)
        {
            return PerformCommand(new ReloadPlugin { Plugin = plugin }, cancellationToken);
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
        public Task<bool> RemoveHttpEndpoint(HttpEndpointType endpointType, string ns, string path, CancellationToken cancellationToken = default)
        {
            return PerformCommand<bool>(new RemoveHttpEndpoint { EndpointType = endpointType, Namespace = ns, Path = path }, cancellationToken);
        }

        /// <summary>
        /// Remove an existing user session
        /// </summary>
        /// <param name="id">Identifier of the session</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if the session could be removed</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="SbcPermissions.ManageUserSessions"/>
        public Task<bool> RemoveUserSession(int id, CancellationToken cancellationToken = default)
        {
            return PerformCommand<bool>(new RemoveUserSession { Id = id }, cancellationToken);
        }

        /// <summary>
        /// Resolve a RepRapFirmware-style file path to a real file path
        /// </summary>
        /// <param name="path">File path to resolve</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Resolved file path</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="SbcPermissions.CommandExecution"/>
        /// <seealso cref="SbcPermissions.FileSystemAccess"/>
        public Task<string> ResolvePath(string path, CancellationToken cancellationToken = default)
        {
            return PerformCommand<string>(new ResolvePath { Path = path }, cancellationToken);
        }

        /// <summary>
        /// Resolve a RepRapFirmware-style file path to a real file path
        /// </summary>
        /// <param name="path">File path to resolve</param>
        /// <param name="baseDirectory">Base directory to resolve the path relative to</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Resolved file path</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="SbcPermissions.CommandExecution"/>
        /// <seealso cref="SbcPermissions.FileSystemAccess"/>
        public Task<string> ResolvePath(string path, FileDirectory baseDirectory, CancellationToken cancellationToken = default)
        {
            return PerformCommand<string>(new ResolvePath { Path = path, BaseDirectory = baseDirectory }, cancellationToken);
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
        [Obsolete("Deprecated in favor of SetObjectModel")]
        public Task<bool> SetMachineModel(string path, string value, CancellationToken cancellationToken = default)
        {
            return PerformCommand<bool>(new SetObjectModel { PropertyPath = path, Value = value }, cancellationToken);
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
        public Task<bool> SetObjectModel(string path, string value, CancellationToken cancellationToken = default)
        {
            return PerformCommand<bool>(new SetObjectModel { PropertyPath = path, Value = value }, cancellationToken);
        }

        /// <summary>
        /// Set custom plugin data in the object model
        /// </summary>
        /// <param name="key">Key to set</param>
        /// <param name="value">Value to set</param>
        /// <param name="plugin">Identifier of the plugin to update (optional)</param>
        /// <param name="cancellationToken"></param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <exception cref="UnauthorizedAccessException">Insufficient permissions to modify other plugin data</exception>
        /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
        /// <seealso cref="SbcPermissions.ManagePlugins"/>
        public Task SetPluginData(string key, JsonElement value, string? plugin = null, CancellationToken cancellationToken = default)
        {
            return PerformCommand(new SetPluginData { Plugin = plugin, Key = key, Value = value }, cancellationToken);
        }

        /// <summary>
        /// Override the current machine status if a software update is in progress
        /// </summary>
        /// <param name="isUpdating">If the machine status is supposed to be overrridden</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <remarks>
        /// The object model must not be locked when this is called
        /// </remarks>
        public Task SetUpdateStatus(bool isUpdating, CancellationToken cancellationToken = default)
        {
            return PerformCommand(new SetUpdateStatus { Updating = isUpdating }, cancellationToken);
        }

        /// <summary>
        /// Start a plugin
        /// </summary>
        /// <param name="plugin">Identifier of the plugin</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="SbcPermissions.ManagePlugins"/>
        public Task StartPlugin(string plugin, CancellationToken cancellationToken = default)
        {
            return PerformCommand(new StartPlugin { Plugin = plugin }, cancellationToken);
        }

        /// <summary>
        /// Stop a plugin
        /// </summary>
        /// <param name="plugin">Identifier of the plugin</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="SbcPermissions.ManagePlugins"/>
        public Task StopPlugin(string plugin, CancellationToken cancellationToken = default)
        {
            return PerformCommand(new StopPlugin { Plugin = plugin }, cancellationToken);
        }

        /// <summary>
        /// Wait for the full machine model to be updated from RepRapFirmware
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        [Obsolete("Deprecated in favor of SyncObjectModel")]
        public Task SyncMachineModel(CancellationToken cancellationToken = default)
        {
            return PerformCommand(new SyncObjectModel(), cancellationToken);
        }

        /// <summary>
        /// Wait for the full object model to be updated from RepRapFirmware
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="SbcPermissions.CommandExecution"/>
        /// <seealso cref="SbcPermissions.ObjectModelRead"/>
        /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
        public Task SyncObjectModel(CancellationToken cancellationToken = default)
        {
            return PerformCommand(new SyncObjectModel(), cancellationToken);
        }

        /// <summary>
        /// Uninstall a plugin
        /// </summary>
        /// <param name="plugin">Identifier of the plugin</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="SbcPermissions.ManagePlugins"/>
        public Task UninstallPlugin(string plugin, CancellationToken cancellationToken = default)
        {
            return PerformCommand(new UninstallPlugin { Plugin = plugin }, cancellationToken);
        }

        /// <summary>
        /// Uninstall a system package
        /// </summary>
        /// <param name="package">Identifier of the package</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="SbcPermissions.SuperUser"/>
        public Task UninstallSystemPackage(string package, CancellationToken cancellationToken = default)
        {
            return PerformCommand(new UninstallSystemPackage { Package = package }, cancellationToken);
        }

        /// <summary>
        /// Unlock the machine model again
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        [Obsolete("Deprecated in favor of UnlockObjectModel")]
        public Task UnlockMachineModel(CancellationToken cancellationToken)
        {
            return PerformCommand(new UnlockObjectModel(), cancellationToken);
        }

        /// <summary>
        /// Write an arbitrary generic message
        /// </summary>
        /// <param name="type">Message type</param>
        /// <param name="message">Message content</param>
        /// <param name="outputMessage">Whether to output the message</param>
        /// <param name="logMessage">Whether to log the message</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="SbcPermissions.CommandExecution"/>
        /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
        [Obsolete("This overload is deprecated because it lacks support for log levels")]
        public Task WriteMessage(MessageType type, string message, bool outputMessage, bool logMessage, CancellationToken cancellationToken = default)
        {
            return PerformCommand(new WriteMessage { Type = type, Content = message, OutputMessage = outputMessage, LogMessage = logMessage }, cancellationToken);
        }

        /// <summary>
        /// Write an arbitrary generic message
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
        public Task WriteMessage(MessageType type, string message, bool outputMessage = true, LogLevel? logLevel = null, CancellationToken cancellationToken = default)
        {
            return PerformCommand(new WriteMessage { Type = type, Content = message, OutputMessage = outputMessage, LogLevel = logLevel }, cancellationToken);
        }

        /// <summary>
        /// Write an arbitrary generic message
        /// </summary>
        /// <param name="message">Message</param>
        /// <param name="outputMessage">Whether to output the message</param>
        /// <param name="logMessage">Whether to log the message</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="SbcPermissions.CommandExecution"/>
        /// <seealso cref="SbcPermissions.ObjectModelReadWrite"/>
        [Obsolete("This overload is deprecated because it lacks support for log levels")]
        public Task WriteMessage(Message message, bool outputMessage = true, bool logMessage = false, CancellationToken cancellationToken = default)
        {
            return PerformCommand(new WriteMessage { Type = message.Type, Content = message.Content, OutputMessage = outputMessage, LogMessage = logMessage }, cancellationToken);
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
        public Task WriteMessage(Message message, bool outputMessage = true, LogLevel logLevel = LogLevel.Off, CancellationToken cancellationToken = default)
        {
            return PerformCommand(new WriteMessage { Type = message.Type, Content = message.Content, OutputMessage = outputMessage, LogLevel = logLevel }, cancellationToken);
        }
    }
}