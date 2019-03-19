using DuetAPI.Commands;
using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Connection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DuetControlServer.IPC
{
    /// <summary>
    /// Wrapper around UNIX socket connections
    /// </summary>
    public sealed class Connection : IDisposable
    {
        private readonly Socket _socket;
        private readonly int _id;
        
        private readonly NetworkStream _networkStream;
        private readonly StreamReader _streamReader;
        private JsonReader _jsonReader;

        /// <summary>
        /// Check the state of the connection
        /// </summary>
        public bool IsConnected
        {
            get => _socket.Connected;
        }

        /// <summary>
        /// Constructor for new connections
        /// </summary>
        /// <param name="socket">New UNIX socket</param>
        /// <param name="id">Connection ID</param>
        public Connection(Socket socket, int id)
        {
            _socket = socket;
            _id = id;

            _networkStream = new NetworkStream(socket);
            _streamReader = new StreamReader(_networkStream);
            InitReader();
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
        /// Read a generic JSON object from the socket
        /// </summary>
        /// <returns>Abstract JObject instance for deserialization</returns>
        public async Task<JObject> ReceiveJson()
        {
            do
            {
                try
                {
                    await _jsonReader.ReadAsync(Program.CancelSource.Token);
                
                    JToken token = await JToken.ReadFromAsync(_jsonReader, Program.CancelSource.Token);
                    if (token.Type == JTokenType.Object)
                    {
                        return (JObject)token;
                    }
                    throw new JsonReaderException();
                }
                catch (JsonReaderException)
                {
                    InitReader();
                    await SendResponse(new IOException("Server received invalid JSON object"));
                }
            }
            while (!Program.CancelSource.IsCancellationRequested);
            
            throw new OperationCanceledException();
        }

        /// <summary>
        /// Receive a fully-populated instance of a BaseCommand from the client
        /// </summary>
        /// <returns>Received command</returns>
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
            command.SourceConnection = _id;
            return command;
        }

        /// <summary>
        /// Check the given command name
        /// </summary>
        /// <param name="name">Name of the command</param>
        /// <returns>Type of the command or null if none found</returns>
        private Type GetCommandType(string name)
        {
            Type result = Processors.Command.SupportedCommands.FirstOrDefault(type => type.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));
            if (result == null)
            {
                result = Processors.Interception.SupportedCommands.FirstOrDefault(type => type.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));
            }
            if (result == null)
            {
                result = Processors.Subscription.SupportedCommands.FirstOrDefault(type => type.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));
            }
            return result;
        }

        /// <summary>
        /// Send a standard Response, EmptyResponse or ErrorResponse asynchronously to the client.
        /// Depending on the type of the object it is encapsulated in a different container.
        /// </summary>
        /// <param name="obj">Object to send</param>
        /// <returns>Asynchronous task</returns>
        public async Task SendResponse(object obj = null)
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
            await _socket.SendAsync(Encoding.UTF8.GetBytes(json + "\n"), SocketFlags.None);
        }
        
        /// <summary>
        /// Send a plain JSON object to the client
        /// </summary>
        /// <param name="obj">Object to send</param>
        /// <returns>Asynchronous task</returns>
        public async Task Send(JObject obj)
        {
            await _socket.SendAsync(Encoding.UTF8.GetBytes(obj.ToString(Formatting.None) + "\n"), SocketFlags.None);
        }

        /// <summary>
        /// Close the connection
        /// </summary>
        public void Close()
        {
            Dispose();
        }

        /// <summary>
        /// Dispose this instance
        /// </summary>
        public void Dispose()
        {
            _streamReader.Dispose();
            _networkStream.Dispose();
        }
    }
}
