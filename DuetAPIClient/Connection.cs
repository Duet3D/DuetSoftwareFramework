using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Machine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DuetAPIClient
{
    /// <summary>
    /// Interface class for simple access to the control server via the Duet API using a UNIX socket
    /// </summary>
    public sealed class Connection : IDisposable
    {
        private readonly ConnectionType _connectionType;
        
        private Socket _unixSocket;
        private NetworkStream _networkStream;
        private StreamReader _streamReader;
        private JsonTextReader _jsonReader;

        /// <summary>
        /// Create a new connection instance
        /// <param name="type">Desired mode of the connection</param>
        /// <seealso cref="Connect(string, CancellationToken)"/>
        /// </summary>
        public Connection(ConnectionType type)
        {
            _connectionType = type;
        }

        /// <summary>
        /// Establishes a connection to the given UNIX socket file
        /// </summary>
        /// <param name="socketPath">Path to the UNIX socket file</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <exception cref="InvalidOperationException">API level is incompatible</exception>
        /// <exception cref="IOException">Connection mode is unavailable</exception>
        public async Task Connect(string socketPath = "/tmp/duet.sock", CancellationToken cancellationToken = default(CancellationToken))
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
                throw new InvalidOperationException($"Incompatible API version (expected {expectedMessage.Version}, got {serverMessage.Version}");
            }

            // Switch mode
            ClientInitMessage clientMessage = new ClientInitMessage { Type = _connectionType };
            await Send(clientMessage);

            BaseResponse response = await ReceiveResponse<object>(cancellationToken);
            if (!response.Success)
            {
                ErrorResponse errorResponse = (ErrorResponse) response;
                throw new IOException($"Could not set connection type {_connectionType} ({errorResponse.ErrorType}: {errorResponse.ErrorMessage})");
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

        #region General purpose commands

        /// <summary>
        /// Instructs the control server to flush all pending commands and to finish all pending moves (like M400 in RepRapFirmware)
        /// </summary>
        /// <seealso cref="DuetAPI.Commands.Flush"/>
        public async Task Flush() => await PerformCommand(new Flush());

        /// <summary>
        /// Parses a G-code file and returns file information about it
        /// </summary>
        /// <param name="fileName">The file to parse</param>
        /// <returns>Retrieved file information</returns>
        /// <seealso cref="GetFileInfo"/>
        public async Task<FileInfo> GetFileInfo(string fileName) => await PerformCommand<FileInfo>(new GetFileInfo { FileName = fileName });

        /// <summary>
        /// Retrieves the full object model of the machine
        /// In subscription mode this is the first command that has to be called once a connection has been established.
        /// </summary>
        /// <returns>The current machine model</returns>
        public async Task<Model> GetMachineModel(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_connectionType == ConnectionType.Subscribe)
            {
                Model model = await Receive<Model>(cancellationToken);
                await Send(new Acknowledge());
                return model;
            }
            return await PerformCommand<Model>(new GetMachineModel());
        }

        /// <summary>
        /// Executes an arbitrary pre-parsed code
        /// </summary>
        /// <param name="code">The code to execute</param>
        /// <returns>The result of the given code</returns>
        /// <seealso cref="Code"/>
        public async Task<CodeResult> PerformCode(Code code) => await PerformCommand<CodeResult>(code);

        /// <summary>
        /// Executes an arbitrary G/M/T-code in text form and returns the result as a string
        /// </summary>
        /// <param name="code">The code to execute</param>
        /// <returns>The code result as a string</returns>
        /// <seealso cref="SimpleCode"/>
        public async Task<string> PerformSimpleCode(string code) => await PerformCommand<string>(new SimpleCode { Code = code });

        #endregion

        #region Interception commands

        /// <summary>
        /// Wait for a code to be intercepted
        /// </summary>
        /// <param name="cancellationToken">An optional cancellation token</param>
        /// <returns>A code that can be intercepted</returns>
        public async Task<Code> ReceiveCode(CancellationToken cancellationToken) => await Receive<Code>(cancellationToken);

        /// <summary>
        /// Resolve a RepRapFirmware-style file path to a real file path
        /// </summary>
        /// <param name="path">File path to resolve</param>
        /// <returns>Resolved file path</returns>
        public async Task<string> ResolvePath(string path) => await PerformCommand<string>(new ResolvePath { Path = path });

        /// <summary>
        /// Instruct the control server to ignore the last received code (in intercepting mode)
        /// </summary>
        /// <seealso cref="Ignore"/>
        public async Task IgnoreCode() => await PerformCommand(new Ignore());

        /// <summary>
        /// Instruct the control server to resolve the last received code with the given message details (in intercepting mode)
        /// </summary>
        /// <param name="type">Type of the resolving message</param>
        /// <param name="content">Content of the resolving message</param>
        /// <seealso cref="Message"/>
        /// <seealso cref="Resolve"/>
        public async Task ResolveCode(MessageType type, string content) => await PerformCommand(new Resolve { Content = content, Type = type });

        #endregion

        #region Subscription commands

        /// <summary>
        /// Receive a (partial) machine model update.
        /// If the subscription mode is set to <see cref="SubscriptionMode.Patch"/>, new update patches of the object model
        /// need to be applied manually. This method is intended to receive such fragments.
        /// </summary>
        /// <param name="cancellationToken">An optional cancellation token</param>
        /// <returns>The partial update JSON</returns>
        /// <seealso cref="GetMachineModel(CancellationToken)"/>
        /// <seealso cref="JsonHelper.PatchObject(object, JObject)"/>
        public async Task<JObject> GetMachineModelPatch(CancellationToken cancellationToken)
        {
            JObject patch = await ReceiveJson(cancellationToken);
            await Send(new Acknowledge());
            return patch;
        }

        #endregion

        private async Task PerformCommand(BaseCommand command)
        {
            await Send(command);

            BaseResponse response = await ReceiveResponse();
            if (!response.Success)
            {
                ErrorResponse errorResponse = (ErrorResponse)response;
                throw new CommandException(command.Command, errorResponse.ErrorType, errorResponse.ErrorMessage);
            }
        }
        
        private async Task<T> PerformCommand<T>(BaseCommand command)
        {
            await Send(command);
            
            BaseResponse response = await ReceiveResponse<T>();
            if (response.Success)
            {
                return ((Response<T>)response).Result;
            }

            ErrorResponse errorResponse = (ErrorResponse)response;
            throw new CommandException(command.Command, errorResponse.ErrorType, errorResponse.ErrorMessage);
        }

        private async Task<T> Receive<T>(CancellationToken cancellationToken = default(CancellationToken))
        {
            JObject obj = await ReceiveJson(cancellationToken);
            return obj.ToObject<T>();
        }

        private async Task<BaseResponse> ReceiveResponse(CancellationToken cancellationToken = default(CancellationToken))
        {
            JObject obj = await ReceiveJson(cancellationToken);
            
            BaseResponse response = obj.ToObject<BaseResponse>();
            if (!response.Success)
            {
                response = obj.ToObject<ErrorResponse>();
            }
            return response;
        }

        private async Task<BaseResponse> ReceiveResponse<T>(CancellationToken cancellationToken = default(CancellationToken))
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

        private async Task<JObject> ReceiveJson(CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                await _jsonReader.ReadAsync(cancellationToken);
                
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

        private async Task Send(object obj)
        {
            string json = JsonConvert.SerializeObject(obj, JsonHelper.DefaultSettings);
            await _unixSocket.SendAsync(Encoding.UTF8.GetBytes(json + "\n"), SocketFlags.None);
        }
    }
}