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
        private readonly Socket unixSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private NetworkStream networkStream;
        private StreamReader streamReader;

        /// <summary>
        /// Establishes a connection to the given UNIX socket file
        /// </summary>
        /// <param name="type">Desired mode of the connection</param>
        /// <param name="socketPath">Path to the UNIX socket file</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <exception cref="IOException">Thrown if the API level is incompatible or if the connection mode is unavailable</exception>
        public async Task Connect(ConnectionType type, string socketPath = "/tmp/duet.sock", CancellationToken cancellationToken = default(CancellationToken))
        {
            // Create a new connection
            UnixDomainSocketEndPoint endPoint = new UnixDomainSocketEndPoint(socketPath);
            unixSocket.Connect(endPoint);

            networkStream = new NetworkStream(unixSocket);
            streamReader = new StreamReader(networkStream);

            // Verify server init message
            ServerInitMessage expectedMessage = new ServerInitMessage();
            ServerInitMessage serverMessage = await Receive<ServerInitMessage>(cancellationToken);
            if (serverMessage.Version < expectedMessage.Version)
            {
                throw new IOException($"Incompatible API version (expected {expectedMessage.Version}, got {serverMessage.Version}");
            }

            // Switch mode
            ClientInitMessage clientMessage = new ClientInitMessage { Type = type };
            await Send(clientMessage);

            BaseResponse response = await ReceiveResponse<object>(cancellationToken);
            if (!response.Success)
            {
                ErrorResponse errorResponse = (ErrorResponse) response;
                throw new IOException($"Could not set connection type {type} ({errorResponse.ErrorType}: {errorResponse.ErrorMessage})");
            }
        }

        /// <summary>
        /// Returns true if the socket is still connected
        /// </summary>
        public bool IsConnected
        {
            get => (unixSocket != null) && (unixSocket.Connected);
        }

        // General purpose commands

        /// <summary>
        /// Executes an arbitrary pre-parsed code
        /// </summary>
        /// <param name="code">The code to execute</param>
        /// <returns>The result of the given code</returns>
        /// <seealso cref="Code"/>
        public async Task<CodeResult> SendCode(Code code) => await PerformCommand<CodeResult>(code);

        /// <summary>
        /// Instructs the control server to flush all pending commands and to finish all pending moves (like M400 in RepRapFirmware)
        /// </summary>
        /// <seealso cref="Flush"/>
        public async Task SendFlush() => await PerformCommand(new Flush());

        /// <summary>
        /// Parses a G-code file and returns file information about it
        /// </summary>
        /// <param name="fileName">The file to parse</param>
        /// <returns>Retrieved file information</returns>
        /// <seealso cref="GetFileInfo"/>
        public async Task<FileInfo> GetFileInfo(string fileName) => await PerformCommand<FileInfo>(new GetFileInfo { FileName = fileName });

        /// <summary>
        /// Retrieves the current object model of the machine
        /// </summary>
        /// <returns>The current machine model</returns>
        /// <seealso cref="GetMachineModel"/>
        public async Task<Model> GetMachineModel() => await PerformCommand<Model>(new GetMachineModel());

        /// <summary>
        /// Executes an arbitrary G/M/T-code in text form and returns the result as a string
        /// </summary>
        /// <param name="code">The code to execute</param>
        /// <returns>The code result as a string</returns>
        /// <seealso cref="SimpleCode"/>
        public async Task<string> SendSimpleCode(string code) => await PerformCommand<string>(new SimpleCode { Code = code });

        // Interception commands
        
        /// <summary>
        /// Wait for a code to be intercepted
        /// </summary>
        /// <param name="cancellationToken">An optional cancellation token</param>
        /// <returns>A code that can be intercepted</returns>
        public async Task<Code> ReceiveCode(CancellationToken cancellationToken) => await Receive<Code>(cancellationToken);

        /// <summary>
        /// Instruct the control server to ignore the last received code (in intercepting mode)
        /// </summary>
        /// <seealso cref="Ignore"/>
        public async Task SendIgnore() => await PerformCommand(new Ignore());

        /// <summary>
        /// Instruct the control server to resolve the last received code with the given message details (in intercepting mode)
        /// </summary>
        /// <param name="type">Type of the resolving message</param>
        /// <param name="content">Content of the resolving message</param>
        /// <seealso cref="Message"/>
        /// <seealso cref="Resolve"/>
        public async Task SendResolve(MessageType type, string content) => await PerformCommand(new Resolve { Content = content, Type = type });
        
        // Subscription commands

        /// <summary>
        /// Receive the full machine model.
        /// This must be called initially after the connection in subscription mode has been established and
        /// repeatable if the mode is set to <see cref="SubscriptionMode.Full"/>
        /// </summary>
        /// <param name="cancellationToken">An optional cancellation token</param>
        /// <returns>The current full object model</returns>
        public async Task<Model> ReceiveMachineModel(CancellationToken cancellationToken) => await Receive<Model>(cancellationToken);

        /// <summary>
        /// Receive a partial machine model update.
        /// If the subscription mode is set to <see cref="SubscriptionMode.Patch"/>, new update patches of the object model
        /// need to be applied manually. This method is intended to receive such fragments.
        /// </summary>
        /// <param name="cancellationToken">An optional cancellation token</param>
        /// <returns>The partial update JSON</returns>
        /// <seealso cref="JsonHelper.PatchObject(object, JObject)"/>
        public async Task<JObject> ReceivePatch(CancellationToken cancellationToken) => await ReceiveJson(cancellationToken);

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

        private async Task<JObject> ReceiveJson(CancellationToken cancellationToken)
        {
            // This cannot become a member of this class because there is no way to reset the
            // JsonTextReader after a parsing error occurs
            using (JsonTextReader jsonReader = new JsonTextReader(streamReader) { CloseInput = false })
            {
                JToken token = await JToken.ReadFromAsync(jsonReader, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                
                if (token.Type == JTokenType.Object)
                {
                    return (JObject)token;
                }
                throw new IOException("Client received invalid JSON object");
            }
        }

        private async Task Send(object obj)
        {
            string json = JsonConvert.SerializeObject(obj, JsonHelper.DefaultSettings);
            await unixSocket.SendAsync(Encoding.UTF8.GetBytes(json + "\n"), SocketFlags.None);
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
            streamReader?.Dispose();
            networkStream?.Dispose();
            unixSocket.Dispose();
        }
    }
}