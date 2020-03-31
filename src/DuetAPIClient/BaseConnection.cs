using System;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using DuetAPI.Machine;
using DuetAPI.Utility;
using DuetAPIClient.Exceptions;

namespace DuetAPIClient
{
    /// <summary>
    /// Base class for connections that access the control server via the Duet API using a UNIX socket
    /// </summary>
    public abstract class BaseConnection : IDisposable
    {
        /// <summary>
        /// Mode of this connection
        /// </summary>
        private readonly ConnectionMode _connectionMode;
        
        /// <summary>
        /// Socket used for inter-process communication
        /// </summary>
        protected readonly Socket _unixSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        /// <summary>
        /// Create a new connection instance
        /// </summary>
        /// <param name="mode">Mode of the new connection</param>
        protected BaseConnection(ConnectionMode mode)
        {
            _connectionMode = mode;
        }

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
            UnixDomainSocketEndPoint endPoint = new UnixDomainSocketEndPoint(socketPath);
            _unixSocket.Connect(endPoint);

            // Verify server init message
            ServerInitMessage ownMessage = new ServerInitMessage();
            ServerInitMessage serverMessage = await Receive<ServerInitMessage>(cancellationToken);
            if (serverMessage.Version < ownMessage.Version)
            {
                throw new IncompatibleVersionException($"Incompatible API version (need {ownMessage.Version}, got {serverMessage.Version}");
            }
            Id = serverMessage.Id;

            // Switch mode
            await Send(initMessage, cancellationToken);

            BaseResponse response = await ReceiveResponse(cancellationToken);
            if (!response.Success)
            {
                ErrorResponse errorResponse = (ErrorResponse)response;
                throw new IOException($"Could not set connection type {_connectionMode} ({errorResponse.ErrorType}: {errorResponse.ErrorMessage})");
            }
        }

        /// <summary>
        /// Returns true if the socket is still connected
        /// </summary>
        public bool IsConnected
        {
            get => !disposed && _unixSocket.Connected;
        }

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
            await Send(command, cancellationToken);

            BaseResponse response = await ReceiveResponse(cancellationToken);
            if (!response.Success)
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
            await Send(command, cancellationToken);
            
            BaseResponse response = await ReceiveResponse<T>(cancellationToken);
            if (response.Success)
            {
                return ((Response<T>)response).Result;
            }

            ErrorResponse errorResponse = (ErrorResponse)response;
            if (errorResponse.ErrorType == nameof(TaskCanceledException))
            {
                throw new TaskCanceledException(errorResponse.ErrorMessage);
            }
            throw new InternalServerException(command.Command, errorResponse.ErrorType, errorResponse.ErrorMessage);
        }

        /// <summary>
        /// Receive a deserialized object from the server
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <typeparam name="T">Type of the received object</typeparam>
        /// <returns>Received object</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        protected async Task<T> Receive<T>(CancellationToken cancellationToken)
        {
            using JsonDocument jsonDocument = await ReceiveJson(cancellationToken);
            if (typeof(T) == typeof(DuetAPI.Machine.MachineModel))
            {
                DuetAPI.Machine.MachineModel newModel = new DuetAPI.Machine.MachineModel();
                newModel.UpdateFromJson(jsonDocument.RootElement);
                return (T)(object)newModel;
            }
            else
            {
                return JsonSerializer.Deserialize<T>(jsonDocument.RootElement.GetRawText(), JsonHelper.DefaultJsonOptions);
            }
        }

        /// <summary>
        /// Receive a base response from the server
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Deserialized base response</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        private async Task<BaseResponse> ReceiveResponse(CancellationToken cancellationToken)
        {
            using JsonDocument jsonDoc = await ReceiveJson(cancellationToken);
            foreach (var item in jsonDoc.RootElement.EnumerateObject())
            {
                if (item.Name.Equals(nameof(BaseResponse.Success), StringComparison.InvariantCultureIgnoreCase) &&
                    item.Value.ValueKind == JsonValueKind.True)
                {
                    // Response OK
                    return JsonSerializer.Deserialize<BaseResponse>(jsonDoc.RootElement.GetRawText(), JsonHelper.DefaultJsonOptions);
                }
            }

            // Error
            return JsonSerializer.Deserialize<ErrorResponse>(jsonDoc.RootElement.GetRawText(), JsonHelper.DefaultJsonOptions);
        }

        /// <summary>
        /// Receive a response from the server
        /// </summary>
        /// <typeparam name="T">Response type</typeparam>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Deserialized response</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        private async Task<BaseResponse> ReceiveResponse<T>(CancellationToken cancellationToken)
        {
            using JsonDocument jsonDoc = await ReceiveJson(cancellationToken);
            foreach (JsonProperty property in jsonDoc.RootElement.EnumerateObject())
            {
                if (property.Name.Equals(nameof(BaseResponse.Success), StringComparison.InvariantCultureIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.True)
                {
                    // Response OK. Check for model objects because they require special deserialization
                    if (typeof(T).IsSubclassOf(typeof(ModelObject)))
                    {
                        foreach (JsonProperty subProperty in jsonDoc.RootElement.EnumerateObject())
                        {
                            if (subProperty.Name.Equals(nameof(Response<T>.Result), StringComparison.InvariantCultureIgnoreCase) &&
                                subProperty.Value.ValueKind != JsonValueKind.Null)
                            {
                                ModelObject result = (ModelObject)Activator.CreateInstance(typeof(T));
                                result = result.UpdateFromJson(subProperty.Value);

                                Response<T> response = (Response<T>)Activator.CreateInstance(typeof(Response<T>));
                                response.Success = true;
                                response.Result = (T)(object)result;
                                return response;
                            }
                        }
                    }

                    // Standard response
                    return JsonSerializer.Deserialize<Response<T>>(jsonDoc.RootElement.GetRawText(), JsonHelper.DefaultJsonOptions);
                }
            }

            // Error
            return JsonSerializer.Deserialize<ErrorResponse>(jsonDoc.RootElement.GetRawText(), JsonHelper.DefaultJsonOptions);
        }

        /// <summary>
        /// Receive partially deserialized object from the server
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Partially deserialized data</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        protected async Task<JsonDocument> ReceiveJson(CancellationToken cancellationToken)
        {
            using MemoryStream json = await JsonHelper.ReceiveUtf8Json(_unixSocket, cancellationToken);
            return await JsonDocument.ParseAsync(json);
        }

        /// <summary>
        /// Serialize an arbitrary object into JSON and send it to the server plus NL
        /// </summary>
        /// <param name="obj">Object to send</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Message could not be processed</exception>
        protected async Task Send(object obj, CancellationToken cancellationToken)
        {
            byte[] jsonToWrite = JsonSerializer.SerializeToUtf8Bytes(obj, obj.GetType(), JsonHelper.DefaultJsonOptions);
            //Console.Write($"OUT {Encoding.UTF8.GetString(jsonToWrite)}");
            await _unixSocket.SendAsync(jsonToWrite, SocketFlags.None, cancellationToken);
            //Console.WriteLine(" OK");
        }
    }
}