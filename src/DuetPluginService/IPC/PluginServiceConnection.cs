using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using DuetAPI.Utility;
using DuetAPIClient;
using DuetPluginService.Commands;
using Microsoft.Extensions.Options;
using System;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DuetPluginService.IPC;

/// <summary>
/// Connection service for plugin management
/// </summary>
/// <param name="commandFactory">Command activator</param>
/// <param name="settings">Settings</param>
public class PluginServiceConnection(CommandFactory commandFactory, IOptions<Settings> settings) : BaseConnection(ConnectionMode.PluginService)
{
    private readonly Settings _settings = settings.Value;

    /// <summary>
    /// Start the plugin service connection
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        PluginServiceInitMessage initMessage = new();
        await ConnectAsync(initMessage, _settings.SocketPath, cancellationToken);
    }

    // <summary>
    /// Receive a command from the control server
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Deserialized command instance</returns>
    /// <exception cref="ArgumentException">Received invalid command</exception>
    /// <exception cref="SocketException">Connection has been closed</exception>
    public async ValueTask<BaseCommand> ReceiveCommandAsync(CancellationToken cancellationToken = default)
    {
        using JsonDocument jsonDocument = await ReceiveJsonDocumentAsync(cancellationToken);
        foreach (JsonProperty item in jsonDocument.RootElement.EnumerateObject())
        {
            if (item.Name.Equals(nameof(BaseCommand.Command), StringComparison.InvariantCultureIgnoreCase))
            {
                // Make sure the received command is a string
                if (item.Value.ValueKind != JsonValueKind.String)
                {
                    throw new ArgumentException("Command type must be a string");
                }

                // Get the command name and deserialize it
                string commandName = item.Value.GetString()!;
                return commandFactory.Create(commandName, jsonDocument.RootElement);
            }
        }
        throw new ArgumentException("Command type not found");
    }

    private static readonly BaseResponse _emptyResponse = new();

    /// <summary>
    /// Send a response to the client. The given object is send either in an empty, error, or standard response body
    /// </summary>
    /// <param name="obj">Object to send</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="SocketException">Message could not be sent</exception>
    public async ValueTask SendResponseAsync(object? obj = null, CancellationToken cancellationToken = default)
    {
        if (obj is null)
        {
            await SendAsync(_emptyResponse, cancellationToken);
        }
        else if (obj is Exception e)
        {
            if (e is AggregateException ae)
            {
                e = ae.InnerException!;
            }
            ErrorResponse errorResponse = new(e);
            await SendAsync(errorResponse, cancellationToken);
        }
        else
        {
            Response<object> response = new(obj);
            await SendAsync(response, cancellationToken);
        }
    }

    /// <summary>
    /// Send a JSON object to the client
    /// </summary>
    /// <param name="obj">Object to send</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="SocketException">Message could not be sent</exception>
    public ValueTask<int> SendAsync(object obj, CancellationToken cancellationToken = default)
    {
        byte[] toSend = (obj is byte[] byteArray) ? byteArray : JsonSerializer.SerializeToUtf8Bytes(obj, JsonHelper.DefaultJsonOptions.GetTypeInfo(obj.GetType()));
        //Console.WriteLine(() => $"Sending {Encoding.UTF8.GetString(toSend)}");
        return _unixSocket.SendAsync(toSend, SocketFlags.None, cancellationToken);
    }
}
