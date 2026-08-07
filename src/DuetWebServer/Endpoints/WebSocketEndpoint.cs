using System;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Connection;
using DuetAPIClient;
using DuetWebServer.Singletons;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;

namespace DuetWebServer.Endpoints;

/// <summary>
/// Minimal-API endpoint for WebSocket requests
/// </summary>
public class WebSocketEndpoint
{
    /// <summary>
    /// PONG response when a PING is received
    /// </summary>
    private static readonly byte[] PONG = Encoding.UTF8.GetBytes("PONG\n");

    /// <summary>
    /// Response that is sent when a command is unsupported
    /// </summary>
    private const string UnsupportedCommandResponse = "Unsupported command. The only supported commands are 'OK' and 'PING'";

    /// <summary>
    /// Register the WebSocket endpoint
    /// </summary>
    /// <param name="app">Web application</param>
    public static void Map(WebApplication app)
    {
        app.MapGet("/machine", Get);
    }

    /// <summary>
    /// WS /machine?sessionKey=XXX&amp;verbose=true&amp;obsolete=true
    /// Provide WebSocket for continuous model updates. This is primarily used to keep DWC up-to-date
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settingsMonitor">Application settings</param>
    /// <param name="applicationLifetime">Application lifecycle instance</param>
    /// <param name="sessionStorage">Session storage singleton</param>
    /// <param name="sessionKey">Optional session key for authentication</param>
    /// <param name="verbose">Whether object model fields flagged as verbose are required</param>
    /// <param name="obsolete">Whether object model fields flagged as obsolete are required</param>
    /// <returns>Asynchronous task</returns>
    private static async Task Get(HttpContext context, ILogger<WebSocketEndpoint> logger, IOptionsMonitor<Settings> settingsMonitor, IHostApplicationLifetime applicationLifetime, ISessionStorage sessionStorage, string? sessionKey, bool verbose = false, bool obsolete = false)
    {
        Settings settings = settingsMonitor.CurrentValue;

        if (!context.WebSockets.IsWebSocketRequest)
        {
            // Not a WebSocket request
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            EndpointHelper.LogWarning(logger, $"{context.Connection.RemoteIpAddress} did not send a WebSocket request");
            return;
        }

        if (!Services.ModelObserver.CheckWebSocketOrigin(context))
        {
            // Origin check failed
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            EndpointHelper.LogWarning(logger, $"Origin check failed for {context.Connection.RemoteIpAddress}");
            return;
        }

        if (string.IsNullOrEmpty(sessionKey))
        {
            try
            {
                using CommandConnection connection = new();
                await connection.ConnectAsync(settings.SocketPath);
                if (!await connection.CheckPasswordAsync(Defaults.Password))
                {
                    // Non-default password set and no sessionKey passed
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    EndpointHelper.LogWarning(logger, $"Machine password is set but WebSocket request from {context.Connection.RemoteIpAddress} had no session key");
                    return;
                }
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException!;
                }
                if (e is IncompatibleVersionException)
                {
                    // Incompatible DCS version
                    context.Response.StatusCode = StatusCodes.Status502BadGateway;
                }
                else if (e is SocketException)
                {
                    // DCS is not started
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                }
                else
                {
                    // Generic error
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                }
                return;
            }
        }
        else if (!sessionStorage.CheckSessionKey(sessionKey, false))
        {
            // Session key passed but it is invalid
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            EndpointHelper.LogWarning(logger, $"WebSocket request from {context.Connection.RemoteIpAddress} passed an invalid session key");
            return;
        }

        // Process the WebSocket request
        try
        {
            if (!string.IsNullOrEmpty(sessionKey))
            {
                sessionStorage.SetWebSocketState(sessionKey, true);
            }
            using WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync();
            await Process(context, logger, settings, applicationLifetime, webSocket, verbose, obsolete);
        }
        finally
        {
            if (!string.IsNullOrEmpty(sessionKey))
            {
                sessionStorage.SetWebSocketState(sessionKey, false);
            }
        }
    }

    /// <summary>
    /// Deal with a newly opened WebSocket.
    /// A client may receive one of the WS codes:
    /// (1001) Endpoint unavailable
    /// (1003) Invalid command
    /// (1011) Internal error
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settings">Application settings</param>
    /// <param name="applicationLifetime">Application lifecycle instance</param>
    /// <param name="webSocket">WebSocket connection</param>
    /// <param name="verbose">Whether object model fields flagged as verbose are required</param>
    /// <param name="obsolete">Whether object model fields flagged as obsolete are required</param>
    /// <returns>Asynchronous task</returns>
    private static async Task Process(HttpContext context, ILogger logger, Settings settings, IHostApplicationLifetime applicationLifetime, WebSocket webSocket, bool verbose, bool obsolete)
    {
        using SubscribeConnection subscribeConnection = new();
        try
        {
            // Subscribe to object model updates targeting the HTTP code channel. Both are fixed for the
            // lifetime of the connection, so a client that changes its mind has to open a new socket
            await subscribeConnection.ConnectAsync(SubscriptionMode.Patch, CodeChannel.HTTP, [], settings.SocketPath, verbose, obsolete);
        }
        catch (Exception e)
        {
            if (e is AggregateException ae)
            {
                e = ae.InnerException!;
            }
            if (e is IncompatibleVersionException)
            {
                EndpointHelper.LogError(logger, "Incompatible DCS version");
                await CloseConnection(webSocket, WebSocketCloseStatus.InternalServerError, "Incompatible DCS version");
                return;
            }
            if (e is SocketException)
            {
                if (File.Exists(settings.StartErrorFile))
                {
                    string startError = await File.ReadAllTextAsync(settings.StartErrorFile);
                    EndpointHelper.LogError(logger, startError);
                    await CloseConnection(webSocket, WebSocketCloseStatus.EndpointUnavailable, startError);
                    return;
                }

                EndpointHelper.LogError(logger, "DCS is not started");
                await CloseConnection(webSocket, WebSocketCloseStatus.EndpointUnavailable, "Failed to connect to Duet, please check your connection (DCS is not started)");
                return;
            }
            EndpointHelper.LogError(logger, e, "Failed to connect to DCS");
            await CloseConnection(webSocket, WebSocketCloseStatus.EndpointUnavailable, e.Message);
            return;
        }

        // Log this event
        string? ipAddress = context.Connection.RemoteIpAddress?.ToString();
        int port = context.Connection.RemotePort;
        EndpointHelper.LogInformation(logger, $"WebSocket connected from {ipAddress}:{port}");

        // Register this client and keep it up-to-date
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime.ApplicationStopping);
        Task? rxTask = null, txTask = null;
        try
        {
            // Fetch full model copy and send it over initially
            await using (MemoryStream json = await subscribeConnection.GetSerializedObjectModelAsync())
            {
                await webSocket.SendAsync(json.ToArray(), WebSocketMessageType.Text, true, default);
            }

            // Deal with this connection in full-duplex mode. All sends must be serialized via a shared
            // lock because WebSocket forbids concurrent SendAsync calls (e.g. PONG vs model patch)
            AsyncAutoResetEvent dataAcknowledged = new();
            AsyncLock sendLock = new();
            rxTask = ReadFromClient(logger, webSocket, dataAcknowledged, sendLock, cts.Token);
            txTask = WriteToClient(webSocket, subscribeConnection, dataAcknowledged, sendLock, cts.Token);

            // Deal with the tasks' lifecycles
            Task terminatedTask = await Task.WhenAny(rxTask, txTask);
            if (terminatedTask.IsFaulted)
            {
                throw terminatedTask.Exception!;
            }
        }
        catch (Exception e)
        {
            if (e is AggregateException ae)
            {
                e = ae.InnerException!;
            }
            if (e is SocketException)
            {
                EndpointHelper.LogError(logger, "DCS has been stopped");
                await CloseConnection(webSocket, WebSocketCloseStatus.EndpointUnavailable, "DCS has been stopped");
            }
            else if (e is OperationCanceledException)
            {
                await CloseConnection(webSocket, WebSocketCloseStatus.EndpointUnavailable, "DWS is shutting down");
            }
            else
            {
                EndpointHelper.LogError(logger, e, $"Connection from {ipAddress}:{port} terminated with an exception");
                await CloseConnection(webSocket, WebSocketCloseStatus.InternalServerError, e.Message);
            }
        }
        finally
        {
            cts.Cancel();

            // Wait for both tasks to finish before the socket is disposed
            try
            {
                await Task.WhenAll(rxTask ?? Task.CompletedTask, txTask ?? Task.CompletedTask);
            }
            catch
            {
                // ignored, the connection is being torn down anyway
            }

            EndpointHelper.LogInformation(logger, $"WebSocket disconnected from {ipAddress}:{port}");
        }
    }

    /// <summary>
    /// Keep reading from the client
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="webSocket">WebSocket to read from</param>
    /// <param name="dataAcknowledged">Event to trigger when the client has acknowledged data</param>
    /// <param name="sendLock">Lock for writing to the WebSocket</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    private static async Task ReadFromClient(ILogger logger, WebSocket webSocket, AsyncAutoResetEvent dataAcknowledged, AsyncLock sendLock, CancellationToken cancellationToken)
    {
        byte[] receiveBuffer = new byte[128];
        do
        {
            WebSocketReceiveResult readResult = await webSocket.ReceiveAsync(receiveBuffer, cancellationToken);
            if (readResult.MessageType == WebSocketMessageType.Close)
            {
                // Remote end is closing this connection
                break;
            }
            if (readResult.MessageType == WebSocketMessageType.Binary)
            {
                // Terminate the connection if binary content is received
                await CloseConnection(webSocket, WebSocketCloseStatus.InvalidMessageType, "Only text commands are supported");
                break;
            }
            if (!readResult.EndOfMessage)
            {
                // Don't allow too long messages
                await CloseConnection(webSocket, WebSocketCloseStatus.InvalidPayloadData, "Message is too long");
                break;
            }

            string[] receivedLines = Encoding.UTF8.GetString(receiveBuffer, 0, readResult.Count).Split('\r', '\n');
            foreach (string line in receivedLines)
            {
                if (line == "OK")
                {
                    // Client is ready to receive the next JSON object
                    dataAcknowledged.Set();
                }
                else if (line == "PING")
                {
                    // Client hasn't received an update in a while, send back a PONG response
                    using (await sendLock.LockAsync(cancellationToken))
                    {
                        await webSocket.SendAsync(PONG, WebSocketMessageType.Text, true, cancellationToken);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    EndpointHelper.LogWarning(logger, $"Received unsupported line from WebSocket: '{line}'");
                    await CloseConnection(webSocket, WebSocketCloseStatus.InvalidMessageType, UnsupportedCommandResponse);
                    break;
                }
            }
        }
        while (webSocket.State == WebSocketState.Open);
    }

    /// <summary>
    /// Keep writing object model updates to the client
    /// </summary>
    /// <param name="webSocket">WebSocket to write to</param>
    /// <param name="subscribeConnection">IPC connection to supply model updates</param>
    /// <param name="dataAcknowledged">Event that is triggered when the client has acknowledged data</param>
    /// <param name="sendLock">Lock for writing to the WebSocket</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    private static async Task WriteToClient(WebSocket webSocket, SubscribeConnection subscribeConnection, AsyncAutoResetEvent dataAcknowledged, AsyncLock sendLock, CancellationToken cancellationToken)
    {
        do
        {
            // Wait for the client to acknowledge the receipt of the last JSON object
            await dataAcknowledged.WaitAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // Wait for another object model update and send it to the client
            await using MemoryStream objectModelPatch = await subscribeConnection.GetSerializedObjectModelAsync(cancellationToken);
            using (await sendLock.LockAsync(cancellationToken))
            {
                await webSocket.SendAsync(objectModelPatch.ToArray(), WebSocketMessageType.Text, true, cancellationToken);
            }
        }
        while (webSocket.State == WebSocketState.Open);
    }

    /// <summary>
    /// Close the WebSocket connection again
    /// </summary>
    /// <param name="webSocket">WebSocket to close</param>
    /// <param name="status">Close status to transmit</param>
    /// <param name="message">Close message</param>
    /// <returns>Asynchronous task</returns>
    private static async Task CloseConnection(WebSocket webSocket, WebSocketCloseStatus status, string message)
    {
        if (webSocket.State == WebSocketState.Open)
        {
            await webSocket.CloseAsync(status, message, default);
        }
    }
}
