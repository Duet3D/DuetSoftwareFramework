using System;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;

namespace DuetAPIClient
{
    /// <summary>
    /// Base class for connections that access the control server via the Duet API using a UNIX socket
    /// </summary>
    /// <remarks>
    /// Create a new connection instance
    /// </remarks>
    /// <param name="mode">Mode of the new connection</param>
    public abstract class BaseConnection(ConnectionMode mode) : IDisposable
    {
        /// <summary>
        /// Mode of this connection
        /// </summary>
        private readonly ConnectionMode _connectionMode = mode;
        
        /// <summary>
        /// Socket used for inter-process communication
        /// </summary>
        protected readonly Socket _unixSocket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        /// <summary>
        /// Finalizer of this class
        /// </summary>
        ~BaseConnection() => Dispose(false);

        /// <summary>
        /// Indicates if this instance has been disposed
        /// </summary>
        private bool disposed;

        /// <summary>
        /// Cleans up the current connection and all resources associated to it
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected dipose implementation
        /// </summary>
        /// <param name="disposing">True if this instance is being disposed</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
            {
                return;
            }

            if (disposing)
            {
                _unixSocket.Dispose();
            }

            disposed = true;
        }

        /// <summary>
        /// Identifier of this connection
        /// </summary>
        /// <seealso cref="Code.SourceConnection"/>
        public int Id { get; private set; }

        /// <summary>
        /// Establishes a connection to the given UNIX socket file
        /// </summary>
        /// <param name="initMessage">Init message to send to the server</param>
        /// <param name="socketPath">Path to the UNIX socket file</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="IncompatibleVersionException">API level is incompatible</exception>
        /// <exception cref="IOException">Connection mode is unavailable</exception>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        protected async Task Connect(ClientInitMessage initMessage, string socketPath, CancellationToken cancellationToken)
        {
            // Create a new connection
            UnixDomainSocketEndPoint endPoint = new(socketPath);
            _unixSocket.Connect(endPoint);

            // Read the server init message
            ServerInitMessage ownMessage = new();
            ServerInitMessage serverMessage = await ReceiveInitMessage(cancellationToken);
            Id = serverMessage.Id;

            // Switch mode
            initMessage.Version = Defaults.ProtocolVersion;
            await SendInitMessage(initMessage, cancellationToken);

            // Check the result
            BaseResponse response = await ReceiveResponse(cancellationToken);
            if (!response.Success)
            {
                ErrorResponse errorResponse = (ErrorResponse)response;
                if (errorResponse.ErrorType == nameof(IncompatibleVersionException))
                {
                    throw new IncompatibleVersionException(errorResponse.ErrorMessage);
                }
                throw new IOException($"Could not set connection type {_connectionMode} ({errorResponse.ErrorType}: {errorResponse.ErrorMessage})");
            }
        }

        /// <summary>
        /// Returns true if the socket is still connected
        /// </summary>
        public bool IsConnected => !disposed && _unixSocket.Connected;

        /// <summary>
        /// Closes the current connection and disposes it
        /// </summary>
        public void Close() => Dispose();

        /// <summary>
        /// Check if the connection is still alive
        /// </summary>
        /// <exception cref="SocketException">Connection is no longer available</exception>
        public void Poll() => _unixSocket.Send(Array.Empty<byte>());

        /// <summary>
        /// Perform an arbitrary command
        /// </summary>
        /// <param name="command">Command to run</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Command result</returns>
        /// <exception cref="InternalServerException">Deserialized internal error from DCS</exception>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        protected async Task PerformCommand(BaseCommand command, CancellationToken cancellationToken)
        {
            await SendCommand(command, cancellationToken);

            BaseResponse? response = await ReceiveResponse(cancellationToken);
            if (response is not null && !response.Success)
            {
                ErrorResponse errorResponse = (ErrorResponse)response;
                if (errorResponse.ErrorType == nameof(TaskCanceledException))
                {
                    throw new TaskCanceledException(errorResponse.ErrorMessage);
                }
                throw new InternalServerException(command.Command, errorResponse.ErrorType, errorResponse.ErrorMessage);
            }
        }

        /// <summary>
        /// Perform an arbitrary command
        /// </summary>
        /// <param name="command">Command to run</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Command result</returns>
        /// <typeparam name="T">Type of the command result</typeparam>
        /// <exception cref="InternalServerException">Deserialized internal error from DCS</exception>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        protected async Task<T> PerformCommand<T>(BaseCommand command, CancellationToken cancellationToken)
        {
            await SendCommand(command, cancellationToken);

            using MemoryStream jsonStream = await JsonHelper.ReceiveUtf8Json(_unixSocket, cancellationToken);
            //Console.Write($"IN {Encoding.UTF8.GetString(jsonStream.ToArray())}");

            T DeserializeResponse()
            {
                Span<byte> jsonSpan = jsonStream.ToArray();
                Utf8JsonReader reader = new(jsonSpan), resultReader = reader;
                bool isSuccess = false, resultSeen = false;

                T FinalDeserialize(ref Utf8JsonReader reader)
                {
                    #if NET9_0_OR_GREATER
                    #warning FIXME use JsonTypeInfoResolver.Combine here
                    #endif
                    return (typeof(T) == typeof(ObjectModel) || typeof(T) == typeof(Message) || typeof(T) == typeof(GCodeFileInfo)) ?
                        (T)JsonSerializer.Deserialize(ref reader, typeof(T), ObjectModelContext.Default)! :
                        (T)JsonSerializer.Deserialize(ref reader, typeof(T), CommandContext.Default)!;
                }

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
                                    return FinalDeserialize(ref resultReader);
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
                                return FinalDeserialize(ref reader);
                            }
                            else
                            {
                                resultSeen = true;
                                resultReader = reader;
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

        /// <summary>
        /// Receive a deserialized object from the server
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <typeparam name="T">Type of the received object</typeparam>
        /// <returns>Received object</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        protected async ValueTask<T> ReceiveCommand<T>(CancellationToken cancellationToken) where T : BaseCommand
        {
            using MemoryStream jsonStream = await JsonHelper.ReceiveUtf8Json(_unixSocket, cancellationToken);
            //Console.Write($"IN {Encoding.UTF8.GetString(jsonStream.ToArray())}");
            return (T)JsonSerializer.Deserialize(jsonStream.ToArray(), typeof(T), CommandContext.Default)!;
        }

        /// <summary>
        /// Receive a base response from the server
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Deserialized base response</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        private async ValueTask<BaseResponse> ReceiveResponse(CancellationToken cancellationToken)
        {
            using MemoryStream jsonStream = await JsonHelper.ReceiveUtf8Json(_unixSocket, cancellationToken);
            //Console.Write($"IN {Encoding.UTF8.GetString(jsonStream.ToArray())}");

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

        /// <summary>
        /// Receive plain JSON document
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Partially deserialized data</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        protected async ValueTask<JsonDocument> ReceiveJsonDocument(CancellationToken cancellationToken)
        {
            await using MemoryStream json = await JsonHelper.ReceiveUtf8Json(_unixSocket, cancellationToken);
            return await JsonDocument.ParseAsync(json, cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Receive init message from the server
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Deserialized response</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        private async ValueTask<ServerInitMessage> ReceiveInitMessage(CancellationToken cancellationToken)
        {
            using MemoryStream jsonStream = await JsonHelper.ReceiveUtf8Json(_unixSocket, cancellationToken);
            //Console.Write($"IN {Encoding.UTF8.GetString(jsonStream.ToArray())}");
            return JsonSerializer.Deserialize(jsonStream.ToArray(), ConnectionContext.Default.ServerInitMessage)!;
        }

        /// <summary>
        /// Serialize an init message and send it to the server
        /// </summary>
        /// <param name="message">Message to send</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Message could not be processed</exception>
        protected async ValueTask SendInitMessage(InitMessage message, CancellationToken cancellationToken)
        {
            byte[] jsonToWrite = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), ConnectionContext.Default);
            //Console.Write($"OUT {Encoding.UTF8.GetString(jsonToWrite)}");
            await _unixSocket.SendAsync(jsonToWrite, SocketFlags.None, cancellationToken);
            //Console.WriteLine(" OK");
        }

        /// <summary>
        /// Serialize a command and send it to the server
        /// </summary>
        /// <param name="command">Command to send</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Message could not be processed</exception>
        protected async ValueTask SendCommand(BaseCommand command, CancellationToken cancellationToken)
        {
            byte[] jsonToWrite = JsonSerializer.SerializeToUtf8Bytes(command, command.GetType(), CommandContext.Default);
            //Console.Write($"OUT {Encoding.UTF8.GetString(jsonToWrite)}");
            await _unixSocket.SendAsync(jsonToWrite, SocketFlags.None, cancellationToken);
            //Console.WriteLine(" OK");
        }
    }
}