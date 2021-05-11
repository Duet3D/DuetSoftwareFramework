using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using DuetAPI.Utility;
using DuetAPIClient;
using System;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DuetPluginService
{
    /// <summary>
    /// Service connection for plugin management.
    /// This type of connection is reserved and should not be used by third-party plugins!
    /// </summary>
    public sealed class PluginServiceConnection : BaseConnection
    {
        /// <summary>
        /// List of supported commands in this mode
        /// </summary>
        public static readonly Type[] SupportedCommands =
        {
            typeof(Commands.InstallPlugin),
            typeof(Commands.StartPlugin),
            typeof(Commands.StopPlugin),
            typeof(Commands.UninstallPlugin),
            typeof(Commands.InstallSystemPackage),
            typeof(Commands.UninstallSystemPackage),
        };

        /// <summary>
        /// Create a new connection in plugin service mode
        /// </summary>
        public PluginServiceConnection() : base(ConnectionMode.PluginService) { }

        /// <summary>
        /// Establish a connection to the given UNIX socket file
        /// </summary>
        /// <param name="socketPath">Path to the UNIX socket file</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="IncompatibleVersionException">API level is incompatible</exception>
        /// <exception cref="IOException">Connection mode is unavailable</exception>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Init message could not be processed</exception>
        public Task Connect(string socketPath = Defaults.FullSocketPath, CancellationToken cancellationToken = default)
        {
            PluginServiceInitMessage initMessage = new();
            return Connect(initMessage, socketPath, cancellationToken);
        }
        
        // <summary>
        /// Receive a command from the control server
        /// </summary>
        /// <returns>Deserialized command instance</returns>
        /// <exception cref="ArgumentException">Received invalid command</exception>
        /// <exception cref="SocketException">Connection has been closed</exception>
        public async ValueTask<BaseCommand> ReceiveCommand()
        {
            using JsonDocument jsonDocument = await ReceiveJson(Program.CancellationToken);
            foreach (JsonProperty item in jsonDocument.RootElement.EnumerateObject())
            {
                if (item.Name.Equals(nameof(BaseCommand.Command), StringComparison.InvariantCultureIgnoreCase))
                {
                    // Make sure the received command is a string
                    if (item.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new ArgumentException("Command type must be a string");
                    }

                    // Check if the received command is valid
                    string commandName = item.Value.GetString();
                    Type commandType = SupportedCommands.FirstOrDefault(item => item.Name.Equals(commandName, StringComparison.InvariantCultureIgnoreCase));
                    if (!typeof(BaseCommand).IsAssignableFrom(commandType))
                    {
                        throw new ArgumentException($"Unsupported command {commandName}");
                    }

                    // Perform final deserialization and assign source identifier to this command
                    return (BaseCommand)jsonDocument.RootElement.ToObject(commandType, JsonHelper.DefaultJsonOptions);
                }
            }
            throw new ArgumentException("Command type not found");
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
                ErrorResponse errorResponse = new(e);
                return Send(errorResponse);
            }

            Response<object> response = new(obj);
            return Send(response);
        }

        /// <summary>
        /// Send a JSON object to the client
        /// </summary>
        /// <param name="obj">Object to send</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="SocketException">Message could not be sent</exception>
        public Task Send(object obj)
        {
            byte[] toSend = (obj is byte[] byteArray) ? byteArray : JsonSerializer.SerializeToUtf8Bytes(obj, obj.GetType(), JsonHelper.DefaultJsonOptions);
            //Console.WriteLine(() => $"Sending {Encoding.UTF8.GetString(toSend)}");
            return _unixSocket.SendAsync(toSend, SocketFlags.None);
        }
    }
}
