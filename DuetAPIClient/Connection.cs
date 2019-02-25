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
    public sealed class Connection : IDisposable
    {
        private readonly Socket unixSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private NetworkStream networkStream;
        private StreamReader streamReader;

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

        public bool IsConnected
        {
            get => (unixSocket != null) && (unixSocket.Connected);
        }

        // General purpose commands
        public async Task<CodeResult> SendCode(Code code) => await PerformCommand<CodeResult>(code);
        public async Task SendFlush() => await PerformCommand(new Flush());
        public async Task<FileInfo> GetFileInfo(string fileName) => await PerformCommand<FileInfo>(new GetFileInfo { FileName = fileName });
        public async Task<Model> GetMachineModel() => await PerformCommand<Model>(new GetMachineModel());
        public async Task<string> SendSimpleCode(string code) => await PerformCommand<string>(new SimpleCode { Code = code });

        // Interception commands
        public async Task<Code> ReceiveCode(CancellationToken cancellationToken) => await Receive<Code>(cancellationToken);
        public async Task SendIgnore() => await PerformCommand(new Ignore());
        public async Task SendResolve(MessageType type, string content) => await PerformCommand(new Resolve { Content = content, Type = type });
        
        // Subscription commands
        public async Task<Model> ReceiveMachineModel(CancellationToken cancellationToken) => await Receive<Model>(cancellationToken);
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

        public void Close()
        {
            Dispose();
        }

        public void Dispose()
        {
            streamReader?.Dispose();
            networkStream?.Dispose();
            unixSocket.Dispose();
        }
    }
}