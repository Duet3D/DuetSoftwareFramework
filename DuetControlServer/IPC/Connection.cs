using DuetAPI.Commands;
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Connection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DuetControlServer.IPC
{
    public sealed class Connection : IDisposable
    {
        private Socket Socket { get; }
        private int Id { get; }
        
        public bool IsConnected
        {
            get => Socket.Connected;
        }

        private readonly NetworkStream networkStream;
        private readonly StreamReader streamReader;

        public Connection(Socket socket, int id)
        {
            Socket = socket;
            Id = id;

            networkStream = new NetworkStream(socket);
            streamReader = new StreamReader(networkStream);
        }

        /// <summary>
        /// Read a generic JSON object from the socket
        /// </summary>
        /// <returns>Abstract JObject instance for deserialization</returns>
        public async Task<JObject> ReceiveJson()
        {
            do
            {
                // This cannot become a member of this class because there is no way to reset the
                // JsonTextReader after a parsing error occurs
                using (JsonTextReader jsonReader = new JsonTextReader(streamReader) { CloseInput = false })
                {
                    JToken token = await JToken.ReadFromAsync(jsonReader, Program.CancelSource.Token);
                    if (token.Type == JTokenType.Object)
                    {
                        return (JObject)token;
                    }
                }
                await Send(new IOException("Server received invalid JSON object"));
            }
            while (!Program.CancelSource.IsCancellationRequested);

            return null;
        }

        /// <summary>
        /// Receive a fully-populated instance of a BaseCommand from the client
        /// </summary>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public async Task<BaseCommand> ReceiveCommand()
        {
            JObject obj = await ReceiveJson();
            if (obj == null)
            {
                // Connection has been terminated
                return null;
            }
            BaseCommand command = obj.ToObject<BaseCommand>();
            
            // Check if the requested command type is valid
            Type commandType = GetCommandType(command.Command);
            if (commandType == null)
            {
                if (command.Command == nameof(BaseCommand))
                {
                    throw new ArgumentException("Invalid command type");
                }
                throw new ArgumentException($"Invalid command type '{command.Command}'");
            }
            if (!typeof(BaseCommand).IsAssignableFrom(commandType))
            {
                throw new ArgumentException("Command is not of type BaseCommand");
            }
            
            // Perform final deserialization and assign source identifier to this command
            command = (BaseCommand)obj.ToObject(commandType);
            command.SourceConnection = Id;
            return command;
        }

        // Get the type of the specified command but preferably from our own namespace
        private Type GetCommandType(string name)
        {
            return Type.GetType($"{nameof(DuetControlServer)}.{nameof(Commands)}.{name}") ?? Type.GetType(name);
        }

        // Send a standard Response, an EmptyResponse or an ErrorResponse asynchronously to the client
        public async Task Send(object obj)
        {
            string json;
            if (obj == null)
            {
                BaseResponse response = new BaseResponse();
                json = JsonConvert.SerializeObject(response, JsonHelper.DefaultSettings);
            }
            else if (obj is Exception e)
            {
                if (e is AggregateException ae)
                {
                    // Assume there is only one inner exception in this AggregateException...
                    e = ae.InnerException;
                }

                ErrorResponse errorResponse = new ErrorResponse(e.GetType().FullName, e.Message);
                json = JsonConvert.SerializeObject(errorResponse, JsonHelper.DefaultSettings);
            }
            else if (obj is ServerInitMessage)
            {
                json = JsonConvert.SerializeObject(obj, JsonHelper.DefaultSettings);
            }
            else
            {
                Response<object> response = new Response<object>(obj);
                json = JsonConvert.SerializeObject(response, JsonHelper.DefaultSettings);
            }
            await Socket.SendAsync(Encoding.UTF8.GetBytes(json + "\n"), SocketFlags.None);
        }

        public void Close()
        {
            Dispose();
        }

        public void Dispose()
        {
            streamReader.Dispose();
            networkStream.Dispose();
        }
    }
}
