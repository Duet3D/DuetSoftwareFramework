using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using DuetAPI.Utility;
using DuetAPIClient.Exceptions;

namespace DuetAPIClient
{
    /// <summary>
    /// Base class for connections that access the control server via the Duet API using a UNIX socket
    /// </summary>
    public abstract class BaseConnection : IDisposable
    {
        private readonly ConnectionMode _connectionMode;
        
        /// <summary>
        /// Socket used for inter-process communication
        /// </summary>
        protected Socket _unixSocket;

        /// <summary>
        /// Create a new connection instance
        /// </summary>
        /// <param name="mode">Mode of the new connection</param>
        protected BaseConnection(ConnectionMode mode)
        {
            _connectionMode = mode;
        }

        /// <summary>
        /// Identifier of this connection
        /// </summary>
        /// <seealso cref="BaseCommand.SourceConnection"/>
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
        protected async Task Connect(ClientInitMessage initMessage, string socketPath, CancellationToken cancellationToken)
        {
            // Create a new connection
            UnixDomainSocketEndPoint endPoint = new UnixDomainSocketEndPoint(socketPath);
            _unixSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _unixSocket.Connect(endPoint);

            // Verify server init message
            ServerInitMessage expectedMessage = new ServerInitMessage();
            ServerInitMessage serverMessage = await Receive<ServerInitMessage>(cancellationToken);
            if (serverMessage.Version < expectedMessage.Version)
            {
                throw new IncompatibleVersionException($"Incompatible API version (expected {expectedMessage.Version}, got {serverMessage.Version}");
            }
            Id = serverMessage.Id;

            // Switch mode
            await Send(initMessage, cancellationToken);

            BaseResponse response = await ReceiveResponse(cancellationToken);
            if (!response.Success)
            {
                ErrorResponse errorResponse = (ErrorResponse) response;
                throw new IOException($"Could not set connection type {_connectionMode} ({errorResponse.ErrorType}: {errorResponse.ErrorMessage})");
            }
        }

        /// <summary>
        /// Returns true if the socket is still connected
        /// </summary>
        public bool IsConnected
        {
            get => (_unixSocket != null) && (_unixSocket.Connected);
        }

        /// <summary>
        /// Closes the current connection and disposes it
        /// </summary>
        public void Close()
        {
            Dispose();
        }

        /// <summary>
        /// Cleans up the current connection and all resources associated to it
        /// </summary>
        public void Dispose()
        {
            _unixSocket?.Dispose();
            _unixSocket = null;
        }

        /// <summary>
        /// Perform an arbitrary command
        /// </summary>
        /// <param name="command">Command to run</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Command result</returns>
        /// <exception cref="TaskCanceledException">Operation has been cancelled</exception>
        /// <exception cref="InternalServerException">Deserialized internal error from DCS</exception>
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
        /// <exception cref="TaskCanceledException">Operation has been cancelled</exception>
        /// <exception cref="InternalServerException">Deserialized internal error from DCS</exception>
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
        protected async Task<T> Receive<T>(CancellationToken cancellationToken)
        {
            using JsonDocument jsonDoc = await ReceiveJson(cancellationToken);
            if (jsonDoc == null)
            {
                return default;
            }

            if (typeof(T) == typeof(DuetAPI.Machine.MachineModel))
            {
                // FIXME: JsonSerializer does not populate readonly properties like ObservableCollections (yet)
                T obj = (T)Activator.CreateInstance(typeof(T));
                JsonPatch.Patch(obj, jsonDoc);
                return obj;
            }
            else
            {
                return JsonSerializer.Deserialize<T>(jsonDoc.RootElement.GetRawText(), JsonHelper.DefaultJsonOptions);
            }
        }

        private async Task<BaseResponse> ReceiveResponse(CancellationToken cancellationToken)
        {
            using JsonDocument jsonDoc = await ReceiveJson(cancellationToken);
            if (jsonDoc == null)
            {
                return null;
            }

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

        private async Task<BaseResponse> ReceiveResponse<T>(CancellationToken cancellationToken)
        {
            using JsonDocument jsonDoc = await ReceiveJson(cancellationToken);
            if (jsonDoc == null)
            {
                return null;
            }

            foreach (var item in jsonDoc.RootElement.EnumerateObject())
            {
                if (item.Name.Equals(nameof(BaseResponse.Success), StringComparison.InvariantCultureIgnoreCase) &&
                    item.Value.ValueKind == JsonValueKind.True)
                {
                    // Response OK
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
        /// <returns>Partially deserialized data or null if the connection is gone</returns>
        /// <exception cref="IOException">Received no or invalid JSON object</exception>
        protected async Task<JsonDocument> ReceiveJson(CancellationToken cancellationToken)
        {
            using MemoryStream json = await JsonHelper.ReceiveUtf8Json(_unixSocket, cancellationToken);
            if (json.Length == 0)
            {
                return null;
            }
            return await JsonDocument.ParseAsync(json);
        }

        /// <summary>
        /// Serialize an arbitrary object into JSON and send it to the server plus NL
        /// </summary>
        /// <param name="obj">Object to send</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="SocketException">Message could not be sent</exception>
        protected async Task Send(object obj, CancellationToken cancellationToken)
        {
            byte[] jsonToWrite = JsonSerializer.SerializeToUtf8Bytes(obj, obj.GetType(), JsonHelper.DefaultJsonOptions);
            //Console.Write($"OUT {Encoding.UTF8.GetString(jsonToWrite)}");
            await _unixSocket.SendAsync(jsonToWrite, SocketFlags.None, cancellationToken);
            //Console.WriteLine(" OK");
        }
    }
}