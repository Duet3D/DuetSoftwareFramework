using DuetAPI.Commands;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace DuetControlServer.IPC.Worker
{
    public class Base
    {
        protected Socket Socket { get; set; }

        private NetworkStream networkStream;
        private StreamReader streamReader;
        private JsonReader jsonReader;

        protected Base(Socket socket)
        {
            Socket = socket;

            networkStream = new NetworkStream(socket);
            streamReader = new StreamReader(networkStream);
            jsonReader = new JsonTextReader(streamReader);
        }

        ~Base()
        {
            jsonReader.Close();
            streamReader.Close();
            networkStream.Close();
        }

        public virtual Task<Base> Work()
        {
            throw new NotImplementedException();
        }

        // Receive a full JSON object asynchronously from the client
        protected async Task<JObject> ReceiveJson()
        {
            do
            {
                JToken token = await JToken.ReadFromAsync(jsonReader, Program.CancelSource.Token);
                if (token.Type == JTokenType.Object)
                {
                    return (JObject)token;
                }
                else
                {
                    await Send(new ArgumentException("Invalid JSON object"));
                }
            }
            while (!Program.CancelSource.IsCancellationRequested);

            return null;
        }

        // Receive a fully-populated BaseCommand instance from the client
        protected async Task<BaseCommand> ReceiveCommand()
        {
            JObject obj = await ReceiveJson();
            if (obj == null)
            {
                // Connection has been terminated
                return null;
            }

            // Deserialize read JSON object into a fully populated BaseCommand instance
            BaseCommand commandBase = obj.ToObject<BaseCommand>();
            Type commandType = GetCommandType(commandBase.Command);
            if (commandType == null)
            {
                throw new ArgumentException("Invalid command type");
            }
            else if (!typeof(BaseCommand).IsAssignableFrom(commandType))
            {
                throw new ArgumentException("Command is not of type BaseCommand");
            }
            return (BaseCommand)obj.ToObject(commandType);
        }

        // Get the type of the specified command but preferrably from our own namespace.
        // This way we get easy access to our overridden Execute() methods (if present)
        private Type GetCommandType(string name)
        {
            return Type.GetType(nameof(DuetControlServer.Commands) + "." + name) ?? Type.GetType(name);
        }

        // Send a standard Response, an EmptyResponse or an ErrorResponse asynchronously to the client
        protected async Task Send(object obj)
        {
            if (obj == null)
            {
                EmptyResponse response = new EmptyResponse();
                string json = JsonConvert.SerializeObject(response);
            }
            else if (obj is Exception)
            {
                Exception e = (obj is AggregateException) ? (obj as AggregateException).InnerException : (obj as Exception);

                ErrorResponse errorResponse = new ErrorResponse(e.GetType().Name, e.Message);
                string json = JsonConvert.SerializeObject(errorResponse);
                await Socket.SendAsync(Encoding.UTF8.GetBytes(json), SocketFlags.None);
            }
            else
            {
                Response response = new Response(obj);
                string json = JsonConvert.SerializeObject(response);
                await Socket.SendAsync(Encoding.UTF8.GetBytes(json), SocketFlags.None);
            }
        }
    }
}
