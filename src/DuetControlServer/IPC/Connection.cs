using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.Utility;
using DuetControlServer.IPC.Processors;
using Command = DuetControlServer.IPC.Processors.Command;

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
                    using MemoryStream json = await JsonHelper.ReceiveUtf8Json(_unixSocket, Program.CancelSource.Token);
                    JsonDocument jsonDocument = await JsonDocument.ParseAsync(json);
                    Logger.Trace(() => $"Received {Encoding.UTF8.GetString(json.ToArray())}");
                    return jsonDocument;
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
        /// Receive a fully-populated instance of a BaseCommand from the client
        /// </summary>
        /// <returns>Received command or null if nothing could be read</returns>
        /// <exception cref="ArgumentException">Received bad command</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        public async Task<BaseCommand> ReceiveCommand()
        {
            using JsonDocument jsonDoc = await ReceiveJson();
            foreach (var item in jsonDoc.RootElement.EnumerateObject())
            {
                if (item.Name.Equals(nameof(BaseCommand.Command), StringComparison.InvariantCultureIgnoreCase))
                {
                    // Make sure the received command is a string
                    if (item.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new ArgumentException("Command type must be a string");
                    }

                    // Check if the received command is valid
                    Type commandType = GetCommandType(item.Value.GetString());
                    if (!typeof(BaseCommand).IsAssignableFrom(commandType))
                    {
                        throw new ArgumentException("Command is not of type BaseCommand");
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
                    BaseCommand command = (BaseCommand)JsonSerializer.Deserialize(jsonDoc.RootElement.GetRawText(), commandType, JsonHelper.DefaultJsonOptions);
                    SetSourceConnection(command);
                    return command;
                }
            }
            throw new ArgumentException("Command type not found");
        }

        /// <summary>
        /// Retrieve the type of a supported command
        /// </summary>
        /// <param name="name">Name of the command</param>
        /// <returns>Type of the command or null if none found</returns>
        private Type GetCommandType(string name)
        {
            Type result = Command.SupportedCommands.FirstOrDefault(type => type.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));
            if (result == null)
            {
                result = Interception.SupportedCommands.FirstOrDefault(type => type.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));
            }
            if (result == null)
            {
                result = Subscription.SupportedCommands.FirstOrDefault(type => type.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));
            }
            return result;
        }

        /// <summary>
        /// Assign the source connection to a command
        /// </summary>
        /// <param name="command">Deserialized command</param>
        private void SetSourceConnection(BaseCommand command)
        {
            if (command is Code code)
            {
                code.SourceConnection = Id;
            }
            else if (command is Commands.SimpleCode simpleCode)
            {
                simpleCode.SourceConnection = Id;
            }
            else if (command is Commands.LockMachineModel lockCommand)
            {
                lockCommand.SourceConnection = Id;
            }
            else if (command is Commands.UnlockMachineModel unlockCommand)
            {
                unlockCommand.SourceConnection = Id;
            }
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
