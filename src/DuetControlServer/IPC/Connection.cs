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
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.IPC.Processors;
using DuetControlServer.Utility;
using LinuxApi;

namespace DuetControlServer.IPC
{
    /// <summary>
    /// Wrapper around UNIX socket connections
    /// </summary>
    public sealed class Connection : IDisposable
    {
        /// <summary>
        /// Counter for new connections
        /// </summary>
        private static int _idCounter = 1;

        /// <summary>
        /// Logger instance
        /// </summary>
        public readonly NLog.Logger Logger;

        /// <summary>
        /// Identifier of this connection
        /// </summary>
        public int Id { get; }

        /// <summary>
        /// API version of the client
        /// </summary>
        public int ApiVersion { get; set; }

        /// <summary>
        /// Name of the connected plugin
        /// </summary>
        public string PluginName { get; private set; }

        /// <summary>
        /// Permissions of this connection
        /// </summary>
        public SbcPermissions Permissions { get; private set; }

        /// <summary>
        /// Socket holding the connection of the UNIX socket
        /// </summary>
        private readonly Socket _unixSocket;

        /// <summary>
        /// Constructor for new connections
        /// </summary>
        /// <param name="socket">New UNIX socket</param>
        public Connection(Socket socket)
        {
            _unixSocket = socket;
            Id = Interlocked.Increment(ref _idCounter);

            Logger = NLog.LogManager.GetLogger($"IPC#{Id}");
        }

        /// <summary>
        /// Get the peer credentials and assign the available permissions
        /// </summary>
        /// <returns>True if permissions could be assigned</returns>
        public async Task<bool> AssignPermissions()
        {
            _unixSocket.GetPeerCredentials(out int pid, out int uid, out int gid);

            // Make sure this is no root process...
            if (uid != 0 && gid != 0)
            {
                // Check what process we are dealing with
                Process peerProcess = Process.GetProcessById(pid);
                string processName = peerProcess?.MainModule?.FileName;
                if (processName == null)
                {
                    Logger.Error("Failed to get process details from pid {0}. Cannot assign any permissions");
                    return false;
                }

                // Check if this is an installed plugin
                string pluginPath = Path.GetFullPath(processName);
                if (pluginPath.StartsWith(Settings.PluginDirectory))
                {
                    pluginPath = processName.Substring(Settings.PluginDirectory.Length);
                    if (pluginPath.StartsWith('/'))
                    {
                        pluginPath = pluginPath.Substring(1);
                    }

                    // Retrieve the corresponding plugin permissions
                    if (pluginPath.Contains('/'))
                    {
                        string pluginName = pluginPath.Substring(0, pluginPath.IndexOf('/'));
                        using (await Model.Provider.AccessReadOnlyAsync())
                        {
                            foreach (PluginManifest plugin in Model.Provider.Get.Plugins)
                            {
                                if (plugin.Name == pluginName)
                                {
                                    PluginName = plugin.Name;
                                    Permissions = plugin.SbcPermissions;
                                    return true;
                                }
                            }
                        }
                    }

                    // Failed to find plugin
                    Logger.Error("Failed to find corresponding plugin for peer application {0}", processName);
                    return false;
                }
            }

            // Grant full permissions
            Logger.Debug("Granting full DSF permissions to external plugin");
            foreach (Enum permission in Enum.GetValues(typeof(SbcPermissions)))
            {
                Permissions |= (SbcPermissions)permission;
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

            _unixSocket.Dispose();

            disposed = true;
        }

        /// <summary>
        /// Indicates if the connection is still available
        /// </summary>
        public bool IsConnected { get => !disposed && _unixSocket.Connected; }

        /// <summary>
        /// Read a generic JSON object from the socket
        /// </summary>
        /// <returns>JsonDocument for deserialization</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        public async Task<JsonDocument> ReceiveJson()
        {
            do
            {
                try
                {
                    using MemoryStream jsonStream = await JsonHelper.ReceiveUtf8Json(_unixSocket, Program.CancellationToken);
                    Logger.Trace(() => $"Received {Encoding.UTF8.GetString(jsonStream.ToArray())}");

                    return await JsonDocument.ParseAsync(jsonStream);
                }
                catch (JsonException e)
                {
                    Logger.Error(e, "Received malformed JSON");
                    await SendResponse(e);
                }
            }
            while (true);
        }

        /// <summary>
        /// Read a plain JSON object as a string from the socket
        /// </summary>
        /// <returns>JsonDocument for deserialization</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        public async Task<string> ReceivePlainJson()
        {
            do
            {
                try
                {
                    using MemoryStream jsonStream = await JsonHelper.ReceiveUtf8Json(_unixSocket, Program.CancellationToken);
                    Logger.Trace(() => $"Received {Encoding.UTF8.GetString(jsonStream.ToArray())}");

                    using StreamReader reader = new StreamReader(jsonStream);
                    return await reader.ReadToEndAsync();
                }
                catch (JsonException e)
                {
                    Logger.Error(e, "Received malformed JSON");
                    await SendResponse(e);
                }
            }
            while (true);
        }

        /// <summary>
        /// Command name mapping for API version <= 8
        /// </summary>
        private static readonly Dictionary<string, string> _legacyCommandMapping = new Dictionary<string, string>
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
        public async Task<BaseCommand> ReceiveCommand()
        {
            using JsonDocument jsonDocument = await ReceiveJson();
            foreach (JsonProperty item in jsonDocument.RootElement.EnumerateObject())
            {
                if (item.Name.Equals(nameof(BaseCommand.Command), StringComparison.InvariantCultureIgnoreCase))
                {
                    // Make sure the received command is a string
                    if (item.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new ArgumentException("Command type must be a string");
                    }

                    // Map it in case we need to retain backwards-compatibility
                    string commandName = item.Value.GetString();
                    if (ApiVersion <= 8 && _legacyCommandMapping.TryGetValue(commandName.ToLowerInvariant(), out string newCommandName))
                    {
                        commandName = newCommandName;
                    }

                    // Check if the received command is valid
                    Type commandType = Base.GetCommandType(commandName);
                    if (!typeof(BaseCommand).IsAssignableFrom(commandType))
                    {
                        throw new ArgumentException($"Unsupported command {commandName}");
                    }

                    // Log this
                    if (commandType == typeof(Acknowledge))
                    {
                        Logger.Trace("Received command {0}", item.Value.GetString());
                    }
                    else
                    {
                        Logger.Debug("Received command {0}", item.Value.GetString());
                    }

                    // Perform final deserialization and assign source identifier to this command
                    BaseCommand command = (BaseCommand)jsonDocument.RootElement.ToObject(commandType, JsonHelper.DefaultJsonOptions);
                    if (command is Commands.IConnectionCommand commandWithSourceConnection)
                    {
                        commandWithSourceConnection.Connection = this;
                    }
                    return command;
                }
            }
            throw new ArgumentException("Command type not found");
        }

        /// <summary>
        /// Send a response to the client. The given object is send either in an empty, error, or standard response body
        /// </summary>
        /// <param name="obj">Object to send</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="SocketException">Message could not be sent</exception>
        public Task SendResponse(object obj = null)
        {
            if (obj == null)
            {
                return Send(new BaseResponse());
            }
            
            if (obj is Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException;
                }
                ErrorResponse errorResponse = new ErrorResponse(e);
                return Send(errorResponse);
            }

            Response<object> response = new Response<object>(obj);
            return Send(response);
        }
        
        /// <summary>
        /// Send a JSON object to the client
        /// </summary>
        /// <param name="obj">Object to send</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="SocketException">Message could not be sent</exception>
        public async Task Send(object obj)
        {
            byte[] toSend = (obj is byte[] byteArray) ? byteArray : JsonSerializer.SerializeToUtf8Bytes(obj, obj.GetType(), JsonHelper.DefaultJsonOptions);
            Logger.Trace(() => $"Sending {Encoding.UTF8.GetString(toSend)}");
            await _unixSocket.SendAsync(toSend, SocketFlags.None);
        }

        /// <summary>
        /// Check if the connection is still alive
        /// </summary>
        /// <exception cref="SocketException">Connection is no longer available</exception>
        public void Poll() => _unixSocket.Send(Array.Empty<byte>());
    }
}
