using DuetAPI.Commands;
using DuetAPI.Utility;
using System;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DuetAPIClient
{
    /// <summary>
    /// Connection class for dealing with requests received from a custom HTTP endpoint
    /// </summary>
    public sealed class HttpEndpointConnection : IDisposable
    {
        /// <summary>
        /// Socket representing the current HTTP connection
        /// </summary>
        private readonly Socket _socket;
        
        /// <summary>
        /// Indicates if the connection is a WebSocket
        /// </summary>
        private readonly bool _isWebSocket;

        /// <summary>
        /// Constructor for a new connection dealing with a single HTTP endpoint request
        /// </summary>
        /// <param name="socket">Connection socket</param>
        /// <param name="isWebSocket">Indicates if the HTTP endpoint is a WebSocket</param>
        /// <remarks>DCS may create new connections and close them immediately again to check if the UNIX socket is still active</remarks>
        public HttpEndpointConnection(Socket socket, bool isWebSocket)
        {
            _socket = socket;
            _isWebSocket = isWebSocket;
        }

        /// <summary>
        /// Indicates if this instance has been disposed
        /// </summary>
        private bool disposed;

        /// <summary>
        /// Disposes this instance
        /// </summary>
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            _socket.Dispose();

            disposed = true;
        }

        /// <summary>
        /// Indicates if the socket is still connected
        /// </summary>
        public bool IsConnected
        {
            get => !disposed && _socket.Connected;
        }

        /// <summary>
        /// Close this connection
        /// </summary>
        public void Close() => Dispose();

        /// <summary>
        /// Read information about the last HTTP request. Note that a call to this method may fail!
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Received HTTP request data</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        public Task<ReceivedHttpRequest> ReadRequest(CancellationToken cancellationToken = default)
        {
            return Receive<ReceivedHttpRequest>(cancellationToken);
        }

        /// <summary>
        /// Send a simple HTTP response to the client and dispose this connection unless it is a WebSocket
        /// </summary>
        /// <param name="statusCode">HTTP code to return</param>
        /// <param name="response">Response data to return</param>
        /// <param name="responseType">Type of data to return</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        /// <remarks>If the underlying connection is a WebSocket, the user must close this connection manually</remarks>
        public async Task SendResponse(int statusCode = 204, string response = "", HttpResponseType responseType = HttpResponseType.StatusCode, CancellationToken cancellationToken = default)
        {
            try
            {
                SendHttpResponse httpResponse = new()
                {
                    StatusCode = statusCode,
                    Response = response,
                    ResponseType = responseType
                };
                await Send(httpResponse, cancellationToken);
            }
            finally
            {
                if (!_isWebSocket)
                {
                    // Close this connection automatically if only one response can be sent
                    Close();
                }
            }
        }

        /// <summary>
        /// Receive a deserialized object
        /// </summary>
        /// <typeparam name="T">OBject type</typeparam>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Deserialized object</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        private async Task<T> Receive<T>(CancellationToken cancellationToken)
        {
            using MemoryStream json = await JsonHelper.ReceiveUtf8Json(_socket, cancellationToken);
            return await JsonSerializer.DeserializeAsync<T>(json, JsonHelper.DefaultJsonOptions, cancellationToken);
        }

        /// <summary>
        /// Send an arbitrary object
        /// </summary>
        /// <param name="obj">Object to send as JSON</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        private async Task Send(object obj, CancellationToken cancellationToken)
        {
            byte[] jsonToWrite = JsonSerializer.SerializeToUtf8Bytes(obj, obj.GetType(), JsonHelper.DefaultJsonOptions);
            await _socket.SendAsync(jsonToWrite, SocketFlags.None, cancellationToken);
        }
    }
}
