using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Commands;
using DuetControlServer.Utility;
using DuetSharedLibrary;
using Microsoft.Extensions.Logging;

namespace DuetControlServer.IPC;

/// <summary>
/// Wrapper around UNIX socket connections
/// </summary>
/// <remarks>
/// Constructor for new connections
/// </remarks>
/// <param name="socket">New UNIX socket</param>
/// <param name="commandFactory">Command factory to create commands</param>
/// <param name="logger">Logger instance</param>
public sealed class Connection(Socket socket, CommandFactory commandFactory, ILogger logger) : IDisposable
{
    /// <summary>
    /// Counter for new connections
    /// </summary>
    private static int _idCounter = 1;

    /// <summary>
    /// Identifier of this connection
    /// </summary>
    public int Id { get; } = Interlocked.Increment(ref _idCounter);

    /// <summary>
    /// API version of the client
    /// </summary>
    /// <seealso cref="Defaults.ProtocolVersion"/>
    public int ApiVersion { get; set; }

    /// <summary>
    /// Name of the connected plugin
    /// </summary>
    public string? PluginId { get; private set; }

    /// <summary>
    /// Permissions of this connection
    /// </summary>
    public SbcPermissions Permissions { get; private set; }

    /// <summary>
    /// Whether the connection is from the root user
    /// </summary>
    public bool IsRoot { get; private set; }

    /// <summary>
    /// Socket holding the connection of the UNIX socket
    /// </summary>
    public Socket UnixSocket { get; } = socket;

    /// <summary>
    /// Get the peer credentials and assign the available permissions
    /// </summary>
    /// <param name="model">Object model</param>
    /// <returns>True if permissions could be assigned</returns>
    public async Task<bool> AssignPermissionsAsync(Model.ObjectModel model)
    {
        UnixSocket.GetPeerCredentials(out int pid, out int uid, out int gid);

        // Root processes get everything; no plugin lookup needed
        if (uid == 0 || gid == 0)
        {
            IsRoot = true;
            Permissions |= SbcPermissions.SuperUser | SbcPermissions.ServicePlugins;
            GrantExternalPermissions();
            logger.LogDebug("IPC#{Id}: Granting full permissions to root process (pid {Pid})", Id, pid);
            return true;
        }

        // If the plugin service is not running we cannot verify plugin identity, so fall through to the external-program
        // policy. This is the dev-mode path where DCS runs standalone without plugin management. In prod DPS kills its
        // children when it terminates, so this check is safe
        int ownUid = ProcessHelpers.GetEffectiveUserID(), ownGid = ProcessHelpers.GetEffectiveGroupID();
        if (!Processors.PluginService.IsConnected(false))
        {
            // This is also the bootstrap path for DPS itself, which needs the internal ServicePlugins
            // permission to register as plugin service
            if ((uid == ownUid || gid == ownGid) && IsDsfService(pid))
            {
                Permissions |= SbcPermissions.ServicePlugins;
            }
            GrantExternalPermissions();
            return true;
        }

        // If a plugin is currently being started its PID may not yet have propagated to the object model - ask DPS first.
        // Skip this for DPS's own PID: DPS is never a plugin, and its internal connections (e.g. SetPluginProcessAsync)
        // arrive while its single command channel is still busy handling the very StartPlugin call that spawned them -
        // asking DPS to resolve itself would enqueue onto that same blocked channel and deadlock
        if (Commands.StartPlugin.IsAnyStarting && pid != Processors.PluginService.ServicePid)
        {
            string? resolvedPluginId = await ResolvePluginViaServiceAsync(pid, false);
            if (resolvedPluginId is not null)
            {
                using (await model.AccessReadOnlyAsync())
                {
                    if (model.Plugins.TryGetValue(resolvedPluginId, out Plugin plugin))
                    {
                        PluginId = plugin.Id;
                        Permissions |= plugin.SbcPermissions;
                        return true;
                    }
                }
            }
        }

        // Fast path: check if the peer PID itself is a known plugin before touching /proc. Only if the peer is not
        // directly a plugin do we walk the process tree so that child processes inherit the parent plugin's permissions
        using (await model.AccessReadOnlyAsync())
        {
            foreach (Plugin plugin in model.Plugins.Values)
            {
                if (plugin.Pid == pid)
                {
                    PluginId = plugin.Id;
                    Permissions |= plugin.SbcPermissions;
                    return true;
                }
            }

            for (int currentPid = ProcessHelpers.GetParentPid(pid); currentPid > 1; currentPid = ProcessHelpers.GetParentPid(currentPid))
            {
                foreach (Plugin plugin in model.Plugins.Values)
                {
                    if (plugin.Pid == currentPid)
                    {
                        PluginId = plugin.Id;
                        Permissions |= plugin.SbcPermissions;
                        return true;
                    }
                }
            }
        }

        // Not a tracked plugin. If the peer shares our uid/gid the only legitimate origins are DSF services (DPS and
        // DWS) living in DCS's directory. Anything else in our user namespace is untracked or tampered-with and must
        // be rejected. A peer with a different uid/gid is a genuinely external program (admin tool running under its
        // own account) and gets the external-program policy
        if (uid == ownUid || gid == ownGid)
        {
            if (!IsDsfService(pid))
            {
                logger.LogWarning("IPC#{Id}: Rejecting untracked peer sharing our user identity (pid {Pid}, uid {Uid}, gid {Gid})", Id, pid, uid, gid);
                return false;
            }
            logger.LogDebug("IPC#{Id}: Granting permissions to sibling DSF service (pid {Pid})", Id, pid);

            // Sibling DSF services additionally need the internal ServicePlugins permission (e.g. DPS calls SetPluginProcess)
            Permissions |= SbcPermissions.ServicePlugins;
        }

        GrantExternalPermissions();
        return true;
    }

    /// <summary>
    /// Check whether the peer process is a permitted DSF service (DCS, DPS or DWS). Returns false on any error
    /// (process gone, executable unreadable, etc.) so anything we cannot positively identify is rejected
    /// </summary>
    /// <param name="pid">Peer process ID</param>
    /// <returns>True if the peer is a recognized service</returns>
    private static bool IsDsfService(int pid)
    {
        try
        {
            string? procPath = ProcessHelpers.GetExecutablePath(pid);
            if (string.IsNullOrEmpty(procPath))
            {
                return false;
            }

            // Make sure it is in the same directory as DCS
            string? peerDir = Path.GetDirectoryName(procPath);
            string? dcsDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (peerDir != dcsDir)
            {
                return false;
            }

            // DPS is matched against its known service PID (captured from its PluginService connection). A plugin
            // re-execing DuetPluginService under LD_PRELOAD would get a different PID than the systemd-launched one.
            // While the service slot is still empty (bootstrap or DPS restart), the path check above must suffice
            // because the PID is only known after DPS has registered - the binary lives in a root-owned directory.
            // DCS and DWS are matched via AT_SECURE=1: the kernel sets this when a binary with file capabilities is
            // exec'd, which causes glibc to ignore LD_PRELOAD / LD_LIBRARY_PATH / LD_AUDIT. The bit is immutable
            // post-exec. DCS appears here for re-invocations like `DuetControlServer -u` that connect back over IPC
            string peerFilename = Path.GetFileNameWithoutExtension(procPath);
            return peerFilename switch
            {
                "DuetPluginService" => Processors.PluginService.ServicePid == 0 || pid == Processors.PluginService.ServicePid,
                "DuetControlServer" or "DuetWebServer" => ProcessHelpers.IsExecSecure(pid),
                _ => false,
            };
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Grant all permissions except SuperUser and the internal ServicePlugins flag. Used for external admin-owned
    /// programs (and in dev mode where DPS is not available to vet plugin ownership)
    /// </summary>
    private void GrantExternalPermissions()
    {
        logger.LogDebug("IPC#{Id}: Granting full DSF permissions to external program", Id);
        foreach (SbcPermissions permission in Enum.GetValues<SbcPermissions>())
        {
            if (permission != SbcPermissions.SuperUser && permission != SbcPermissions.ServicePlugins)
            {
                Permissions |= permission;
            }
        }
    }

    /// <summary>
    /// Ask the plugin service to resolve the given PID against its tracked plugin processes. Closes the window
    /// where a plugin has started but <c>SetPluginProcessAsync</c> has not yet updated the object model
    /// </summary>
    /// <param name="pid">PID to resolve</param>
    /// <param name="asRoot">Whether to query the root plugin service</param>
    /// <returns>Plugin id if matched, null otherwise</returns>
    private async Task<string?> ResolvePluginViaServiceAsync(int pid, bool asRoot)
    {
        try
        {
            ResolvePluginProcess command = new() { Pid = pid };
            return await Processors.PluginService.PerformCommandAsync<string>(command, asRoot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "IPC#{Id}: Failed to query plugin service", Id);
            return null;
        }
    }

    /// <summary>
    /// Resolve the peer PID to a plugin ID via the matching plugin service (root or non-root). Used by commands
    /// that need to identify the caller's plugin when <see cref="PluginId"/> was not assigned at connect time
    /// (notably root-owned plugins, which skip the PID lookup in <see cref="AssignPermissionsAsync"/>). Caches
    /// the result on <see cref="PluginId"/> so later permission checks can treat the connection as owner
    /// </summary>
    /// <returns>Plugin id if matched, null otherwise</returns>
    public async Task<string?> ResolvePeerPluginIdAsync()
    {
        if (PluginId is not null)
        {
            return PluginId;
        }
        UnixSocket.GetPeerCredentials(out int pid, out _, out _);
        string? resolved = await ResolvePluginViaServiceAsync(pid, IsRoot);
        if (resolved is not null)
        {
            PluginId = resolved;
        }
        return resolved;
    }

    /// <summary>
    /// Cached permission attributes per command type
    /// </summary>
    private static readonly ConcurrentDictionary<Type, RequiredPermissionsAttribute?> _requiredPermissions = new();

    /// <summary>
    /// Get the cached permissions attribute of the given command type
    /// </summary>
    /// <param name="commandType">Command type to look up</param>
    /// <returns>Permissions attribute or null if the command does not have one</returns>
    private static RequiredPermissionsAttribute? GetRequiredPermissions(Type commandType) => _requiredPermissions.GetOrAdd(commandType, static type => type.GetCustomAttribute<RequiredPermissionsAttribute>());

    /// <summary>
    /// Check if any of the given commands may be executed by this connection
    /// </summary>
    /// <param name="supportedCommands">List of supported commands</param>
    /// <returns>True if any command may be executed</returns>
    public bool CheckCommandPermissions(Type[] supportedCommands)
    {
        foreach (Type commandType in supportedCommands)
        {
            RequiredPermissionsAttribute? permissionsAttribute = GetRequiredPermissions(commandType);
            if (permissionsAttribute is not null && permissionsAttribute.Check(Permissions))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Check if the current permissions are sufficient to execute this command
    /// </summary>
    /// <param name="commandType">Command type to check</param>
    /// <exception cref="UnauthorizedAccessException">Permissions are insufficient</exception>
    public void CheckPermissions(Type commandType)
    {
        RequiredPermissionsAttribute? permissionsAttribute = GetRequiredPermissions(commandType);
        if (permissionsAttribute is not null && !permissionsAttribute.Check(Permissions))
        {
            throw new UnauthorizedAccessException("Insufficient permissions");
        }
    }

    /// <summary>
    /// Indicates if the connection has been disposed
    /// </summary>
    private bool disposed;

    /// <summary>
    /// Dispose this connection
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        UnixSocket.Dispose();

        disposed = true;
    }

    /// <summary>
    /// Indicates if the connection is still available
    /// </summary>
    public bool IsConnected => !disposed && UnixSocket.Connected;

    /// <summary>
    /// Reused buffer for incoming JSON messages so each receive does not allocate a new stream.
    /// Sharing it per connection is safe because the protocol permits only one reader at a time
    /// </summary>
    private readonly MemoryStream _receiveStream = new();

    /// <summary>
    /// Receive the next JSON message into <see cref="_receiveStream"/>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Received UTF-8 JSON</returns>
    private async ValueTask<ReadOnlyMemory<byte>> ReceiveJsonAsync(CancellationToken cancellationToken)
    {
        _receiveStream.SetLength(0);
        await JsonHelper.ReceiveUtf8JsonAsync(UnixSocket, _receiveStream, cancellationToken);
        return _receiveStream.GetBuffer().AsMemory(0, (int)_receiveStream.Length);
    }

    /// <summary>
    /// Read a generic response from the socket asynchronously
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Deserialized base response</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Connection has been closed</exception>
    public async ValueTask<BaseResponse> ReceiveResponseAsync(CancellationToken cancellationToken)
    {
        do
        {
            try
            {
                ReadOnlyMemory<byte> receivedJson = await ReceiveJsonAsync(cancellationToken);
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace("IPC#{Id}: Received {Json}", Id, Encoding.UTF8.GetString(receivedJson.Span));
                }

                BaseResponse DeserializeResponse()
                {
                    ReadOnlySpan<byte> jsonSpan = receivedJson.Span;
                    Utf8JsonReader reader = new(jsonSpan);
                    if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                    {
                        throw new ArgumentException("expected start of object");
                    }
                    while (reader.TokenType != JsonTokenType.EndObject && reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.PropertyName)
                        {
                            if (reader.ValueTextEquals("success"u8) && reader.Read())
                            {
                                if (reader.TokenType == JsonTokenType.True)
                                {
                                    return JsonSerializer.Deserialize(jsonSpan, CommandContext.Default.BaseResponse)!;
                                }
                                else if (reader.TokenType == JsonTokenType.False)
                                {
                                    return JsonSerializer.Deserialize(jsonSpan, CommandContext.Default.ErrorResponse)!;
                                }
                                else
                                {
                                    throw new ArgumentException("success must be a boolean");
                                }
                            }
                            else
                            {
                                reader.Skip();
                            }
                        }
                        else
                        {
                            reader.Skip();
                        }
                    }
                    throw new ArgumentException("missing success key");
                }
                return DeserializeResponse();
            }
            catch (JsonException e)
            {
                logger.LogError(e, "IPC#{Id}: Received malformed JSON", Id);
                await SendExceptionAsync(e);
            }
        }
        while (true);
    }

    /// <summary>
    /// Send a command and await its typed result.
    /// Mirrors <c>BaseConnection.PerformCommandAsync&lt;T&gt;</c> on the client side
    /// </summary>
    /// <typeparam name="T">Expected result type (must be registered in <see cref="CommandContext"/>)</typeparam>
    /// <param name="command">Command to send</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Deserialized result</returns>
    /// <exception cref="InternalServerException">Server reported an error response</exception>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Connection has been closed</exception>
    public async Task<T?> PerformCommandAsync<T>(BaseCommand command, CancellationToken cancellationToken)
    {
        await SendCommandAsync(command);

        ReadOnlyMemory<byte> receivedJson = await ReceiveJsonAsync(cancellationToken);
        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("IPC#{Id}: Received {Json}", Id, Encoding.UTF8.GetString(receivedJson.Span));
        }

        ReadOnlySpan<byte> jsonSpan = receivedJson.Span;
        Utf8JsonReader reader = new(jsonSpan), resultReader = reader;
        bool isSuccess = false, resultSeen = false;

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new ArgumentException("expected start of object");
        }
        while (reader.TokenType != JsonTokenType.EndObject && reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("success"u8) && reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.True)
                    {
                        if (resultSeen)
                        {
                            return (T?)JsonSerializer.Deserialize(ref resultReader, typeof(T), CommandContext.Default);
                        }
                        isSuccess = true;
                    }
                    else if (reader.TokenType == JsonTokenType.False)
                    {
                        ErrorResponse errorResponse = JsonSerializer.Deserialize(jsonSpan, CommandContext.Default.ErrorResponse)!;
                        throw new InternalServerException(command.Command, errorResponse.ErrorType, errorResponse.ErrorMessage);
                    }
                    else
                    {
                        throw new ArgumentException("success must be a boolean");
                    }
                }
                else if (reader.ValueTextEquals("result"u8) && reader.Read())
                {
                    if (isSuccess)
                    {
                        return (T?)JsonSerializer.Deserialize(ref reader, typeof(T), CommandContext.Default);
                    }
                    resultSeen = true;
                    resultReader = reader;
                }
                else
                {
                    reader.Skip();
                }
            }
            else
            {
                reader.Skip();
            }
        }
        if (isSuccess)
        {
            // Success without result field
            return default;
        }
        throw new ArgumentException("missing success key");
    }

    /// <summary>
    /// Read a client init message from the socket
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Client init message</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Connection has been closed</exception>
    public async ValueTask<ClientInitMessage> ReceiveInitMessageAsync(CancellationToken cancellationToken)
    {
        do
        {
            try
            {
                ReadOnlyMemory<byte> receivedJson = await ReceiveJsonAsync(cancellationToken);
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace("IPC#{Id}: Received {Json}", Id, Encoding.UTF8.GetString(receivedJson.Span));
                }

                ClientInitMessage DeserializeInitMessage()
                {
                    ReadOnlySpan<byte> jsonSpan = receivedJson.Span;
                    Utf8JsonReader reader = new(jsonSpan);
                    if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                    {
                        throw new ArgumentException("expected start of object");
                    }
                    while (reader.TokenType != JsonTokenType.EndObject && reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.PropertyName)
                        {
                            if (reader.ValueTextEquals("mode"u8) && reader.Read())
                            {
                                if (reader.TokenType != JsonTokenType.String)
                                {
                                    throw new ArgumentException("mode must be a string");
                                }

                                return JsonSerializer.Deserialize(ref reader, ConnectionContext.Default.ConnectionMode) switch
                                {
                                    ConnectionMode.Command => JsonSerializer.Deserialize(jsonSpan, ConnectionContext.Default.CommandInitMessage)!,
                                    ConnectionMode.Intercept => JsonSerializer.Deserialize(jsonSpan, ConnectionContext.Default.InterceptInitMessage)!,
                                    ConnectionMode.Subscribe => JsonSerializer.Deserialize(jsonSpan, ConnectionContext.Default.SubscribeInitMessage)!,
                                    ConnectionMode.CodeStream => JsonSerializer.Deserialize(jsonSpan, ConnectionContext.Default.CodeStreamInitMessage)!,
                                    ConnectionMode.PluginService => JsonSerializer.Deserialize(jsonSpan, ConnectionContext.Default.PluginServiceInitMessage)!,
                                    _ => throw new ArgumentException("Invalid connection mode")
                                };
                            }
                            else
                            {
                                reader.Skip();
                            }
                        }
                        else
                        {
                            reader.Skip();
                        }
                    }
                    throw new ArgumentException("missing connection mode");
                }

                return DeserializeInitMessage();
            }
            catch (JsonException e)
            {
                logger.LogError(e, "IPC#{Id}: Received malformed JSON", Id);
                await SendExceptionAsync(e);
            }
        }
        while (true);
    }

    /// <summary>
    /// Command name mapping for API version 8 or lower
    /// </summary>
    private static readonly Dictionary<string, string> _legacyCommandMapping = new()
    {
        { "getmachinemodel", "GetObjectModel" },
        { "patchmachinemodel", "PatchObjectModel" }
    };

    /// <summary>
    /// Receive a fully-populated instance of a BaseCommand from the client
    /// </summary>
    /// <param name="supportedCommands">List of supported commands</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Received command or null if nothing could be read</returns>
    /// <exception cref="ArgumentException">Received bad command</exception>
    /// <exception cref="SocketException">Connection has been closed</exception>
    public async ValueTask<BaseCommand> ReceiveCommandAsync(Type[] supportedCommands, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> receivedJson = await ReceiveJsonAsync(cancellationToken);
        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("IPC#{Id}: Received {JSON}", Id, Encoding.UTF8.GetString(receivedJson.Span));
        }

        BaseCommand DeserializeCommand()
        {
            ReadOnlySpan<byte> jsonSpan = receivedJson.Span;
            Utf8JsonReader reader = new(jsonSpan);

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw new ArgumentException("Received malformed JSON");
            }

            while (reader.TokenType != JsonTokenType.EndObject && reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("command"u8) && reader.Read())
                    {
                        // Make sure the received command is a string
                        if (reader.TokenType != JsonTokenType.String)
                        {
                            throw new ArgumentException("command must be a string");
                        }

                        // Map it in case we need to retain backwards-compatibility
                        string commandName = reader.GetString()!;
                        if (ApiVersion <= 8 && _legacyCommandMapping.TryGetValue(commandName.ToLowerInvariant(), out string? newCommandName))
                        {
                            commandName = newCommandName;
                        }

                        // Check if the received command is valid
                        Type? commandType = supportedCommands.FirstOrDefault(item => item.Name.Equals(commandName, StringComparison.InvariantCultureIgnoreCase));
                        if (!typeof(BaseCommand).IsAssignableFrom(commandType))
                        {
                            throw new ArgumentException($"unsupported command {commandName}");
                        }

                        // Log this
                        if (commandType == typeof(Acknowledge))
                        {
                            logger.LogTrace("IPC#{Id}: Received command {Command}", Id, commandName);
                        }
                        else
                        {
                            logger.LogDebug("IPC#{Id}: Received command {Command}", Id, commandName);
                        }

                        // Perform final deserialization and assign source identifier to this command
                        reader = new Utf8JsonReader(jsonSpan);
                        BaseCommand command = commandFactory.Create(commandName, ref reader, supportedCommands);
                        if (command is IConnectionCommand commandWithSourceConnection)
                        {
                            commandWithSourceConnection.Connection = this;
                        }
                        return command;
                    }
                }
                else
                {
                    reader.Skip();
                }
            }
            throw new ArgumentException("command type not found");
        }

        return DeserializeCommand();
    }

    private static readonly byte[] _successResponse = Encoding.UTF8.GetBytes("{\"success\":true}");

    /// <summary>
    /// Start of a success response with a result, see <see cref="SendResponseAsync"/>
    /// </summary>
    internal static readonly byte[] SuccessResponseStart = Encoding.UTF8.GetBytes("{\"success\":true,\"result\":");

    /// <summary>
    /// End of a success response with a result, see <see cref="SendResponseAsync"/>
    /// </summary>
    internal static readonly byte[] SuccessResponseEnd = Encoding.UTF8.GetBytes("}");

    /// <summary>
    /// Send a success response to the client
    /// </summary>
    /// <param name="result">Object to send</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="SocketException">Message could not be sent</exception>
    public async ValueTask SendResponseAsync(object? result = null)
    {
        if (result == null)
        {
            logger.LogSendingSuccessResponse(Id);
            await UnixSocket.SendAsync(_successResponse.AsMemory(), SocketFlags.None);
        }
        else
        {
            byte[] rawResult = JsonSerializer.SerializeToUtf8Bytes(result, JsonHelper.DefaultJsonOptions);
            await UnixSocket.SendAsync(new ArraySegment<byte>[] { new(SuccessResponseStart), new(rawResult), new(SuccessResponseEnd) }, SocketFlags.None);
        }
    }

    /// <summary>
    /// Send an exception to the client. The given object is send either in an empty, error, or standard response body
    /// </summary>
    /// <param name="e">Exception to send</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="SocketException">Message could not be sent</exception>
    public Task SendExceptionAsync(Exception e)
    {
        if (e is AggregateException ae)
        {
            e = ae.InnerException!;
        }
        byte[] toSend = JsonSerializer.SerializeToUtf8Bytes(new ErrorResponse(e), CommandContext.Default.ErrorResponse);
        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("IPC#{Id}: Sending {JSON}", Id, Encoding.UTF8.GetString(toSend));
        }
        
        return UnixSocket.SendAsync(toSend, SocketFlags.None);
    }

    /// <summary>
    /// Cached DuetAPI base types used to serialize outgoing commands
    /// </summary>
    private static readonly ConcurrentDictionary<Type, Type> _serializationTypes = new();

    /// <summary>
    /// Send a command to the client
    /// </summary>
    /// <param name="command">Command to send</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="SocketException">Message could not be sent</exception>
    public Task SendCommandAsync(BaseCommand command)
    {
        // Get base type for serialization
        Type baseType = _serializationTypes.GetOrAdd(command.GetType(), static type =>
        {
            while (type.Assembly != typeof(BaseCommand).Assembly)
            {
                type = type.BaseType!;
            }
            return type;
        });

        // Serialize and send the command
        byte[] toSend = JsonSerializer.SerializeToUtf8Bytes(command, baseType, CommandContext.Default);
        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("IPC#{Id}: Sending {JSON}", Id, Encoding.UTF8.GetString(toSend));
        }
        return UnixSocket.SendAsync(toSend, SocketFlags.None);
    }

    /// <summary>
    /// Send raw data to the client
    /// </summary>
    /// <param name="data">Data to send</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="SocketException">Message could not be sent</exception>
    public async ValueTask SendRawDataAsync(ReadOnlyMemory<byte> data)
    {
        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("IPC#{Id}: Sending {JSON}", Id, Encoding.UTF8.GetString(data.Span));
        }
        await UnixSocket.SendAsync(data, SocketFlags.None);
    }

    /// <summary>
    /// Send an init message to the client
    /// </summary>
    /// <param name="msg">Message to send</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="SocketException">Message could not be sent</exception>
    public Task SendInitMessageAsync(InitMessage msg)
    {
        byte[] toSend = JsonSerializer.SerializeToUtf8Bytes(msg, msg.GetType(), ConnectionContext.Default);
        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("IPC#{Id}: Sending {JSON}", Id, Encoding.UTF8.GetString(toSend));
        }
        return UnixSocket.SendAsync(toSend, SocketFlags.None);
    }

    /// <summary>
    /// Check if the connection is still alive
    /// </summary>
    /// <exception cref="SocketException">Connection is no longer available</exception>
    public void Poll() => UnixSocket.Send(Array.Empty<byte>());

    /// <summary>
    /// Close the socket before shutting down
    /// </summary>
    public void Close() => UnixSocket.Shutdown(SocketShutdown.Send);
}
