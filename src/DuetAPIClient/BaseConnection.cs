using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using DuetAPI.Utility;
using DuetAPIClient.Exceptions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DuetAPIClient
{
    /// <summary>
    /// Base class for connections that access the control server via the Duet API using a UNIX socket
    /// </summary>
    public abstract class BaseConnection : IDisposable
    {
        private readonly ConnectionMode _connectionMode;
        
        private Socket _unixSocket;
        private NetworkStream _networkStream;
        private StreamReader _streamReader;
        private JsonTextReader _jsonReader;

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

            // Make sure we can deserialize incoming data
            _networkStream = new NetworkStream(_unixSocket);
            _streamReader = new StreamReader(_networkStream);
            InitReader();

            // Verify server init message
            ServerInitMessage expectedMessage = new ServerInitMessage();
            ServerInitMessage serverMessage = await Receive<ServerInitMessage>(cancellationToken);
            if (serverMessage.Version < expectedMessage.Version)
            {
                throw new IncompatibleVersionException($"Incompatible API version (expected {expectedMessage.Version}, got {serverMessage.Version}");
            }
            Id = serverMessage.Id;

            // Switch mode
            await Send(initMessage);

            BaseResponse response = await ReceiveResponse<object>(cancellationToken);
            if (!response.Success)
            {
                ErrorResponse errorResponse = (ErrorResponse) response;
                throw new IOException($"Could not set connection type {_connectionMode} ({errorResponse.ErrorType}: {errorResponse.ErrorMessage})");
            }
        }

        private void InitReader()
        {
            _jsonReader = new JsonTextReader(_streamReader)
            {
                CloseInput = false,
                SupportMultipleContent = true
            };
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
            _streamReader?.Dispose();
            _networkStream?.Dispose();
            _unixSocket?.Dispose();
        }

        /// <summary>
        /// Perform an arbitrary command
        /// </summary>
        /// <param name="command">Command to run</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Command result</returns>
        /// <exception cref="InternalServerException">Deserialized internal error from DCS</exception>
        protected async Task PerformCommand(BaseCommand command, CancellationToken cancellationToken)
        {
            await Send(command);

            BaseResponse response = await ReceiveResponse(cancellationToken);
            if (!response.Success)
            {
                ErrorResponse errorResponse = (ErrorResponse)response;
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
        protected async Task<T> PerformCommand<T>(BaseCommand command, CancellationToken cancellationToken)
        {
            await Send(command);
            
            BaseResponse response = await ReceiveResponse<T>(cancellationToken);
            if (response.Success)
            {
                return ((Response<T>)response).Result;
            }

            ErrorResponse errorResponse = (ErrorResponse)response;
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
            JObject obj = await ReceiveJson(cancellationToken);
            return obj.ToObject<T>();
        }

        private async Task<BaseResponse> ReceiveResponse(CancellationToken cancellationToken)
        {
            JObject obj = await ReceiveJson(cancellationToken);
            
            BaseResponse response = obj.ToObject<BaseResponse>();
            if (!response.Success)
            {
                response = obj.ToObject<ErrorResponse>();
            }
            return response;
        }

        private async Task<BaseResponse> ReceiveResponse<T>(CancellationToken cancellationToken)
        {
            JObject obj = await ReceiveJson(cancellationToken);

            BaseResponse response = obj.ToObject<BaseResponse>();
            if (response.Success)
            {
                response = obj.ToObject<Response<T>>();
            }
            else
            {
                response = obj.ToObject<ErrorResponse>();
            }
            return response;
        }

        /// <summary>
        /// Receive partially deserialized object from the server
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Partially deserialized data or null if the connection is gone</returns>
        /// <exception cref="IOException">Received no or invalid JSON object</exception>
        protected async Task<JObject> ReceiveJson(CancellationToken cancellationToken)
        {
            try
            {
                if (!await _jsonReader.ReadAsync(cancellationToken))
                {
                    throw new IOException("Could not read data from socket");
                }
                
                JToken token = await JToken.ReadFromAsync(_jsonReader, cancellationToken);
                if (token.Type == JTokenType.Object)
                {
                    return (JObject)token;
                }
                throw new IOException("Client received invalid JSON object");
            }
            catch (JsonReaderException)
            {
                InitReader();
                throw;
            }
        }

        /// <summary>
        /// Receive a serialized object from the server
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Plain JSON</returns>
        protected async Task<string> ReceiveSerializedJson(CancellationToken cancellationToken)
        {
#if true
            // It is not possible to read directly from the stream reader because that leaves us with
            // incomplete data. So we need to partially deserialize+serialize everything here - unfortunately.
            // This is even the case when the JsonTextReader is created and used in a local scope.
            JObject obj = await ReceiveJson(cancellationToken);
            return obj.ToString(Formatting.None);
#else
            StringBuilder builder = new StringBuilder();
            bool inJson = false, inQuotes = false, isEscaped = false;
            int numBraces = 0;
            
            char[] readData = new char[1];
            while (await _streamReader.ReadAsync(readData, cancellationToken) > 0 && (!inJson || numBraces > 0))
            {
                char c = readData[0];
                
                if (inQuotes)
                {
                    if (isEscaped)
                    {
                        isEscaped = false;
                    }
                    else if (c == '\\')
                    {
                        isEscaped = true;
                    }
                    else if (c == '"')
                    {
                        inQuotes = false;
                    }
                }
                else if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == '{')
                {
                    inJson = true;
                    numBraces++;
                }
                else if (c == '}')
                {
                    numBraces--;
                }

                if (inJson)
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
#endif
        }

        /// <summary>
        /// Get a serialized object from a response container from the server.
        /// If an internal error is reported, the received JSON is deserialized and the reported error is thrown.
        /// </summary>
        /// <param name="command">Name of the previously sent command</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Plain JSON</returns>
        /// <exception cref="InternalServerException">Deserialized internal error from DCS</exception>
        protected async Task<string> ReceiveSerializedJsonResponse(string command, CancellationToken cancellationToken)
        {
#if true
            // It is not possible to read directly from the stream reader because that leaves us with
            // incomplete data. So we need to partially deserialize+serialize everything here - unfortunately.
            // This is even the case when the JsonTextReader is created and used in a local scope.
            JObject obj = await ReceiveJson(cancellationToken);
            if (obj.TryGetValue(nameof(ErrorResponse.ErrorType), out JToken errorType) &&
                obj.TryGetValue(nameof(ErrorResponse.ErrorMessage), out JToken errorMessage))
            {
                throw new InternalServerException(command, errorType.Value<string>(), errorMessage.Value<string>());
            }
            return obj.ToString(Formatting.None);
#else
            StringBuilder builder = new StringBuilder();
            
            bool inJson = false, inQuotes = false, isEscaped = false, isError = false;
            int numBraces = 0;
            string quoteValue = "";
            
            char[] readData = new char[1];
            while (await _streamReader.ReadAsync(readData, cancellationToken) > 0 && (!inJson || numBraces > 0))
            {
                char c = readData[0];
                
                if (inQuotes)
                {
                    if (isEscaped)
                    {
                        isEscaped = false;
                    }
                    else if (c == '\\')
                    {
                        isEscaped = true;
                    }
                    else if (c == '"')
                    {
                        inQuotes = false;
                    }
                    else if (numBraces == 1)
                    {
                        quoteValue += c;
                    }
                }
                else if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == '{')
                {
                    inJson = true;
                    numBraces++;
                }
                else if (c == '}')
                {
                    numBraces--;
                    if (numBraces == 1)
                    {
                        builder.Append('}');
                    }
                }

                if (inJson)
                {
                    // Look for error reports in the response body
                    if (numBraces == 1 && !inQuotes && quoteValue.Length > 0)
                    {
                        isError |= quoteValue.Equals(nameof(ErrorResponse.ErrorType), StringComparison.InvariantCultureIgnoreCase);
                        quoteValue = "";
                    }
                    else if (numBraces > 1)
                    {
                        builder.Append(c);
                    }
                }
            }

            string json = builder.ToString();
            if (isError)
            {
                JToken token = JToken.Parse(json);
                if (token.Type == JTokenType.Object)
                {
                    ErrorResponse response = token.ToObject<ErrorResponse>();
                    throw new InternalServerException(command, response.ErrorType, response.ErrorMessage);
                }
            }
            return builder.ToString();
#endif
        }

        /// <summary>
        /// Serialize an arbitrary object into JSON and send it to the server plus NL
        /// </summary>
        /// <param name="obj">Object to send</param>
        /// <returns>Asynchronous task</returns>
        protected async Task Send(object obj)
        {
            string json = JsonConvert.SerializeObject(obj, JsonHelper.DefaultSettings);
            await _unixSocket.SendAsync(Encoding.UTF8.GetBytes(json + "\n"), SocketFlags.None);
        }
    }
}