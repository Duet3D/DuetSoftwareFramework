using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.Connection.InitMessages;
using DuetAPI.Utility;
using DuetControlServer.IPC.Processors;
using Command = DuetControlServer.IPC.Processors.Command;

namespace DuetControlServer.IPC
{
    /// <summary>
    /// Wrapper around UNIX socket connections
    /// </summary>
    public sealed class Connection
    {
        private readonly Socket _unixSocket;

        /// <summary>
        /// Identifier of this connection
        /// </summary>
        public int Id { get; }

        /// <summary>
        /// Constructor for new connections
        /// </summary>
        /// <param name="socket">New UNIX socket</param>
        /// <param name="id">Connection ID</param>
        public Connection(Socket socket, int id)
        {
            _unixSocket = socket;
            Id = id;
        }

        /// <summary>
        /// Read a generic JSON object from the socket
        /// </summary>
        /// <returns>JsonDocument for deserialization or null if the connection has been closed</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        public async Task<JsonDocument> ReceiveJson()
        {
            do
            {
                try
                {
                    using MemoryStream json = await JsonHelper.ReceiveUtf8Json(_unixSocket);
                    if (json.Length == 0)
                    {
                        return null;
                    }
                    return await JsonDocument.ParseAsync(json);
                }
                catch (JsonException e)
                {
                    Console.WriteLine($"[warn] Received bad JSON: {e}");
                    await SendResponse(e);
                }
            }
            while (!Program.CancelSource.IsCancellationRequested);

            throw new OperationCanceledException();
        }

        /// <summary>
        /// Receive a fully-populated instance of a BaseCommand from the client
        /// </summary>
        /// <returns>Received command or null if nothing could be read</returns>
        /// <exception cref="ArgumentException">Bad command received</exception>
        public async Task<BaseCommand> ReceiveCommand()
        {
            JsonDocument jsonDoc = await ReceiveJson();
            if (jsonDoc == null)
            {
                // Connection has been terminated
                return null;
            }

            foreach (var item in jsonDoc.RootElement.EnumerateObject())
            {
                if (item.Name.Equals(nameof(BaseCommand.Command), StringComparison.InvariantCultureIgnoreCase))
                {
                    if (item.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new ArgumentException("Command type must be a string");
                    }

                    Type commandType = GetCommandType(item.Value.GetString());
                    if (!typeof(BaseCommand).IsAssignableFrom(commandType))
                    {
                        throw new ArgumentException("Command is not of type BaseCommand");
                    }

                    // Perform final deserialization and assign source identifier to this command
                    BaseCommand command = (BaseCommand)JsonSerializer.Deserialize(jsonDoc.RootElement.GetRawText(), commandType, JsonHelper.DefaultJsonOptions);
                    command.SourceConnection = Id;
                    return command;
                }
            }

            throw new ArgumentException("Command type not found");
        }

        /// <summary>
        /// Check the given command name
        /// </summary>
        /// <param name="name">Name of the command</param>
        /// <returns>Type of the command or null if none found</returns>
        private Type GetCommandType(string name)
        {
            Type result = Command.SupportedCommands.FirstOrDefault(type => type.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));
            if (result == null)
            {
                result = Interception.SupportedCommands.FirstOrDefault(type => type.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));
            }
            if (result == null)
            {
                result = Subscription.SupportedCommands.FirstOrDefault(type => type.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));
            }
            return result;
        }

        /// <summary>
        /// Send a response to the client. The given object is send either in an empty, error, or standard response body
        /// </summary>
        /// <param name="obj">Object to send</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="SocketException">Message could not be sent</exception>
        public Task SendResponse(object obj = null)
        {
            if (obj == null)
            {
                return Send(new BaseResponse());
            }
            
            if (obj is Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException;
                }
                ErrorResponse errorResponse = new ErrorResponse(e);
                return Send(errorResponse);
            }

            Response<object> response = new Response<object>(obj);
            return Send(response);
        }
        
        /// <summary>
        /// Send a JSON object to the client
        /// </summary>
        /// <param name="obj">Object to send</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="SocketException">Message could not be sent</exception>
        public async Task Send(object obj)
        {
            byte[] toSend = (obj is byte[] byteArray) ? byteArray : JsonSerializer.SerializeToUtf8Bytes(obj, obj.GetType(), JsonHelper.DefaultJsonOptions);
            //Console.Write($"OUT {Encoding.UTF8.GetString(toSend)}");
            await _unixSocket.SendAsync(toSend, SocketFlags.None);
            //Console.WriteLine(" OK");
        }
    }
}
