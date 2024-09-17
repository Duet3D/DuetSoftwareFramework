using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.IPC.Processors;
using LinuxApi;

namespace DuetControlServer.IPC
{
    /// <summary>
    /// Wrapper around UNIX socket connections
    /// </summary>
    /// <remarks>
    /// Constructor for new connections
    /// </remarks>
    /// <param name="socket">New UNIX socket</param>
    public sealed class Connection(Socket socket) : IDisposable
    {
        /// <summary>
        /// Counter for new connections
        /// </summary>
        private static int _idCounter = 1;

        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Identifier of this connection
        /// </summary>
        public int Id { get; } = Interlocked.Increment(ref _idCounter);

        /// <summary>
        /// API version of the client
        /// </summary>
        /// <seealso cref="DuetAPI.Connection.Defaults.ProtocolVersion"/>
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
        /// <returns>True if permissions could be assigned</returns>
        public async Task<bool> AssignPermissions()
        {
            UnixSocket.GetPeerCredentials(out int pid, out int uid, out int gid);

            // Check if the remote program is running as root
            if (uid == 0 || gid == 0)
            {
                IsRoot = true;
                Permissions |= SbcPermissions.SuperUser;
            }

            // Assign permissions based on previously launched plugins
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                foreach (Plugin plugin in Model.Provider.Get.Plugins.Values)
                {
                    if (plugin.Pid == pid)
                    {
                        PluginId = plugin.Id;
                        Permissions |= plugin.SbcPermissions;
                        return true;
                    }
                }
            }

            // If the remote process is running as dsf, reject it unless the process is in the same directory as DCS (like DWS or DPS)
            if (!IsRoot && (uid == LinuxApi.Commands.GetEffectiveUserID() || gid == LinuxApi.Commands.GetEffectiveGroupID()))
            {
                string dcsDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!;
                string remoteDirectory = Path.GetDirectoryName(Process.GetProcessById(pid)?.MainModule?.FileName)!;
                if (dcsDirectory != remoteDirectory)
                {
                    _logger.Error("IPC#{0}: Failed to find plugin permissions for pid #{1}", Id, pid);
                    return false;
                }
            }

            // Grant full permissions to other programs
            _logger.Debug("IPC#{0}: Granting full DSF permissions to external plugin", Id);
            foreach (Enum permission in Enum.GetValues(typeof(SbcPermissions)))
            {
                if (!permission.Equals(SbcPermissions.SuperUser))
                {
                    Permissions |= (SbcPermissions)permission;
                }
            }
            return true;
        }

        /// <summary>
        /// Check if any of the given commands may be executed by this connection
        /// </summary>
        /// <param name="supportedCommands">List of supported commands</param>
        /// <returns>True if any command may be executed</returns>
        public bool CheckCommandPermissions(Type[] supportedCommands)
        {
            foreach (Type commandType in supportedCommands)
            {
                foreach (Attribute attribute in Attribute.GetCustomAttributes(commandType))
                {
                    if (attribute is RequiredPermissionsAttribute permissionsAttribute && permissionsAttribute.Check(Permissions))
                    {
                        return true;
                    }
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
            foreach (Attribute attribute in Attribute.GetCustomAttributes(commandType))
            {
                if (attribute is RequiredPermissionsAttribute permissionsAttribute && !permissionsAttribute.Check(Permissions))
                {
                    throw new UnauthorizedAccessException("Insufficient permissions");
                }
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
        /// Read a generic JSON object from the socket
        /// </summary>
        /// <returns>JsonDocument for deserialization</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        public async ValueTask<JsonDocument> ReceiveJsonDocument()
        {
            do
            {
                try
                {
                    await using MemoryStream jsonStream = await JsonHelper.ReceiveUtf8Json(UnixSocket, Program.CancellationToken);
                    _logger.Trace(() => $"IPC#{Id}: Received {Encoding.UTF8.GetString(jsonStream.ToArray())}");

                    return await JsonDocument.ParseAsync(jsonStream);
                }
                catch (JsonException e)
                {
                    _logger.Error(e, "IPC#{0}: Received malformed JSON", Id);
                    await SendResponse(e);
                }
            }
            while (true);
        }

        /// <summary>
        /// Read a generic response from the socket
        /// </summary>
        /// <returns>Deserialized base response</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        public async ValueTask<BaseResponse> ReceiveResponse()
        {
            do
            {
                try
                {
                    using MemoryStream jsonStream = await JsonHelper.ReceiveUtf8Json(UnixSocket, Program.CancellationToken);
                    _logger.Trace(() => $"IPC#{Id}: Received {Encoding.UTF8.GetString(jsonStream.ToArray())}");

                    BaseResponse DeserializeResponse()
                    {
                        Span<byte> jsonSpan = jsonStream.ToArray();
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
                                        return JsonSerializer.Deserialize(jsonSpan, Commands.CommandContext.Default.BaseResponse)!;
                                    }
                                    else if (reader.TokenType == JsonTokenType.False)
                                    {
                                        return JsonSerializer.Deserialize(jsonSpan, Commands.CommandContext.Default.ErrorResponse)!;
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
                    _logger.Error(e, "IPC#{0}: Received malformed JSON", Id);
                    await SendResponse(e);
                }
            }
            while (true);
        }

        /// <summary>
        /// Read a client init message from the socket
        /// </summary>
        /// <returns>Client init message</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        public async ValueTask<ClientInitMessage> ReceiveInitMessage()
        {
            do
            {
                try
                {
                    using MemoryStream jsonStream = await JsonHelper.ReceiveUtf8Json(UnixSocket, Program.CancellationToken);
                    _logger.Trace(() => $"IPC#{Id}: Received {Encoding.UTF8.GetString(jsonStream.ToArray())}");

                    ClientInitMessage DeserializeInitMessage()
                    {
                        Span<byte> jsonSpan = jsonStream.ToArray();
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
                    _logger.Error(e, "IPC#{0}: Received malformed JSON", Id);
                    await SendResponse(e);
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
            { "lockmachinemodel", "LockObjectModel" },
            { "patchmachinemodel", "PatchObjectModel" },
            { "setmachinemodel", "SetObjectModel" },
            { "unlockmachinemodel", "UnlockObjectModel" }
        };

        /// <summary>
        /// Receive a fully-populated instance of a BaseCommand from the client
        /// </summary>
        /// <returns>Received command or null if nothing could be read</returns>
        /// <exception cref="ArgumentException">Received bad command</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        public async ValueTask<BaseCommand> ReceiveCommand()
        {
            using MemoryStream receivedJson = await JsonHelper.ReceiveUtf8Json(UnixSocket, Program.CancellationToken);
            _logger.Trace(() => $"IPC#{Id}: Received {Encoding.UTF8.GetString(receivedJson.ToArray())}");

            BaseCommand DeserializeCommand()
            {
                Span<byte> jsonSpan = receivedJson.ToArray();
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
                            if (ApiVersion <= 8 && _legacyCommandMapping.TryGetValue(commandName?.ToLowerInvariant() ?? string.Empty, out string? newCommandName))
                            {
                                commandName = newCommandName;
                            }

                            // Check if the received command is valid
                            Type? commandType = Base.GetCommandType(commandName!);
                            if (!typeof(BaseCommand).IsAssignableFrom(commandType))
                            {
                                throw new ArgumentException($"unsupported command {commandName}");
                            }

                            // Log this
                            if (commandType == typeof(Acknowledge))
                            {
                                _logger.Trace("IPC#{0}: Received command {1}", Id, commandName);
                            }
                            else
                            {
                                _logger.Debug("IPC#{0}: Received command {1}", Id, commandName);
                            }

                            // Perform final deserialization and assign source identifier to this command
                            BaseCommand command = (BaseCommand)JsonSerializer.Deserialize(jsonSpan, commandType, Commands.CommandContext.Default)!;
                            if (command is Commands.IConnectionCommand commandWithSourceConnection)
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
        /// Send a success response to the client
        /// </summary>
        /// <param name="result">Object to send</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="SocketException">Message could not be sent</exception>
        public async Task SendResponse(object? result = null)
        {
            if (result == null)
            {
                _logger.Trace(() => $"IPC#{Id}: Sending {Encoding.UTF8.GetString(_successResponse)}");
                await UnixSocket.SendAsync(_successResponse, SocketFlags.None);
            }
            else
            {
                using MemoryStream ms = new();
                using (Utf8JsonWriter writer = new(ms))
                {
                    writer.WriteStartObject();
                    writer.WriteBoolean("success", true);
                    writer.WritePropertyName("result");
                    writer.WriteRawValue(JsonSerializer.SerializeToUtf8Bytes(result, JsonHelper.DefaultJsonOptions), true);
                    writer.WriteEndObject();
                }
                await UnixSocket.SendAsync(ms.ToArray());
            }
        }

        /// <summary>
        /// Send an exception to the client. The given object is send either in an empty, error, or standard response body
        /// </summary>
        /// <param name="e">Exception to send</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="SocketException">Message could not be sent</exception>
        public Task SendException(Exception e)
        {
            if (e is AggregateException ae)
            {
                e = ae.InnerException!;
            }
            byte[] toSend = JsonSerializer.SerializeToUtf8Bytes(new ErrorResponse(e), Commands.CommandContext.Default.ErrorResponse);
            _logger.Trace(() => $"IPC#{Id}: Sending {Encoding.UTF8.GetString(toSend)}");
            return UnixSocket.SendAsync(toSend, SocketFlags.None);
        }

        /// <summary>
        /// Send a command to the client
        /// </summary>
        /// <param name="command">Command to send</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="SocketException">Message could not be sent</exception>
        public Task SendCommand(BaseCommand command)
        {
            byte[] toSend = JsonSerializer.SerializeToUtf8Bytes(command, command.GetType(), Commands.CommandContext.Default);
            _logger.Trace(() => $"IPC#{Id}: Sending {Encoding.UTF8.GetString(toSend)}");
            return UnixSocket.SendAsync(toSend, SocketFlags.None);
        }

        /// <summary>
        /// Send raw data to the client
        /// </summary>
        /// <param name="data">Data to send</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="SocketException">Message could not be sent</exception>
        public Task SendRawData(byte[] data)
        {
            _logger.Trace(() => $"IPC#{Id}: Sending {Encoding.UTF8.GetString(data)}");
            return UnixSocket.SendAsync(data, SocketFlags.None);
        }

        /// <summary>
        /// Send an init message to the client
        /// </summary>
        /// <param name="msg">Message to send</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="SocketException">Message could not be sent</exception>
        public Task SendInitMessage(InitMessage msg)
        {
            byte[] toSend = JsonSerializer.SerializeToUtf8Bytes(msg, msg.GetType(), ConnectionContext.Default);
            _logger.Trace(() => $"IPC#{Id}: Sending {Encoding.UTF8.GetString(toSend)}");
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
}
