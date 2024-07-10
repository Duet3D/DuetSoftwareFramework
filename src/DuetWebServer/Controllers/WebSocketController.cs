using System;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Connection;
using DuetAPIClient;
using DuetWebServer.Singletons;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nito.AsyncEx;

namespace DuetWebServer.Controllers
{
    /// <summary>
    /// MVC controller for WebSocket requests
    /// </summary>
    [ApiController]
    [Route("machine")]
    public class WebSocketController : ControllerBase
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
        /// App settings
        /// </summary>
        private readonly Settings _settings;

        #region Logging
        /// <summary>
        /// Logger instance
        /// </summary>
        private readonly ILogger _logger;

        /// <summary>
        /// Log an information
        /// </summary>
        /// <param name="message">Message</param>
        /// <param name="memberName">Method calling this method</param>
        private void LogInformation(string message, [CallerMemberName] string memberName = "")
        {
            _logger.LogInformation("[{method}] {message}", memberName, message);
        }

        /// <summary>
        /// Log a warning
        /// </summary>
        /// <param name="message">Message</param>
        /// <param name="memberName">Method calling this method</param>
        private void LogWarning(string message, [CallerMemberName] string memberName = "")
        {
            _logger.LogWarning("[{method}] {message}", memberName, message);
        }

        /// <summary>
        /// Log an error
        /// </summary>
        /// <param name="message">Message</param>
        /// <param name="memberName">Method calling this method</param>
        private void LogError(string message, [CallerMemberName] string memberName = "")
        {
            _logger.LogError("[{method}] {message}", memberName, message);
        }

        /// <summary>
        /// Log an error
        /// </summary>
        /// <param name="exception">Exception</param>
        /// <param name="message">Message</param>
        /// <param name="memberName">Method calling this method</param>
        private void LogError(Exception? exception, string message, [CallerMemberName] string memberName = "")
        {
            _logger.LogError(exception, "[{method}] {message}", memberName, message);
        }
        #endregion

        /// <summary>
        /// Host application lifetime
        /// </summary>
        private readonly IHostApplicationLifetime _applicationLifetime;

        /// <summary>
        /// Constructor of a new WebSocket controller
        /// </summary>
        /// <param name="configuration">Configuration of this application</param>
        /// <param name="logger">Logger instance</param>
        /// <param name="applicationLifetime">Application lifecycle instance</param>
        public WebSocketController(IConfiguration configuration, ILogger<WebSocketController> logger, IHostApplicationLifetime applicationLifetime)
        {
            _settings = configuration.Get<Settings>() ?? new();
            _logger = logger;
            _applicationLifetime = applicationLifetime;
        }

        /// <summary>
        /// WS /machine?sessionKey=XXX
        /// Provide WebSocket for continuous model updates. This is primarily used to keep DWC up-to-date
        /// </summary>
        /// <param name="sessionKey">Optional session key for authentication</param>
        /// <param name="sessionStorage">Session storage singleton</param>
        /// <returns>
        /// HTTP status code:
        /// (101) WebSocket upgrade
        /// (400) Bad request
        /// (403) Forbidden
        /// (500) Generic error
        /// (502) Incompatible DCS version
        /// (503) DCS is not started
        /// </returns>
        [HttpGet]
        public async Task Get(string? sessionKey, [FromServices] ISessionStorage sessionStorage)
        {
            if (!HttpContext.WebSockets.IsWebSocketRequest)
            {
                // Not a WebSocket request
                HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                LogWarning($"{HttpContext.Connection.RemoteIpAddress} did not send a WebSocket request");
                return;
            }

            if (!Services.ModelObserver.CheckWebSocketOrigin(HttpContext))
            {
                // Origin check failed
                HttpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                LogWarning($"Origin check failed for {HttpContext.Connection.RemoteIpAddress}");
                return;
            }

            if (string.IsNullOrEmpty(sessionKey))
            {
                try
                {
                    using CommandConnection connection = new();
                    await connection.Connect(_settings.SocketPath);
                    if (!await connection.CheckPassword(Defaults.Password))
                    {
                        // Non-default password set and no sessionKey passed
                        HttpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                        LogWarning($"Machine password is set but WebSocket request from {HttpContext.Connection.RemoteIpAddress} had no session key");
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
                        HttpContext.Response.StatusCode = StatusCodes.Status502BadGateway;
                    }
                    else if (e is SocketException)
                    {
                        // DCS is not started
                        HttpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    }
                    else
                    {
                        // Generic error
                        HttpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    }
                    return;
                }
            }
            else if (!sessionStorage.CheckSessionKey(sessionKey, false))
            {
                // Session key passed but it is invalid
                HttpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                LogWarning($"WebSocket request from {HttpContext.Connection.RemoteIpAddress} passed an invalid session key");
                return;
            }

            // Process the WebSocket request
            try
            {
                if (!string.IsNullOrEmpty(sessionKey))
                {
                    sessionStorage.SetWebSocketState(sessionKey, true);
                }
                using WebSocket webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
                await Process(webSocket);
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
        /// <param name="webSocket">WebSocket connection</param>
        /// <returns>Asynchronous task</returns>
        private async Task Process(WebSocket webSocket)
        {
            using SubscribeConnection subscribeConnection = new();
            try
            {
                // Subscribe to object model updates targeting the HTTP code channel
                await subscribeConnection.Connect(SubscriptionMode.Patch, CodeChannel.HTTP, Array.Empty<string>(), _settings.SocketPath);
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException!;
                }
                if (e is IncompatibleVersionException)
                {
                    LogError("Incompatible DCS version");
                    await CloseConnection(webSocket, WebSocketCloseStatus.InternalServerError, "Incompatible DCS version");
                    return;
                }
                if (e is SocketException)
                {
                    if (System.IO.File.Exists(_settings.StartErrorFile))
                    {
                        string startError = await System.IO.File.ReadAllTextAsync(_settings.StartErrorFile);
                        LogError(startError);
                        await CloseConnection(webSocket, WebSocketCloseStatus.EndpointUnavailable, startError);
                        return;
                    }

                    LogError("DCS is not started");
                    await CloseConnection(webSocket, WebSocketCloseStatus.EndpointUnavailable, "Failed to connect to Duet, please check your connection (DCS is not started)");
                    return;
                }
                LogError(e, "Failed to connect to DCS");
                await CloseConnection(webSocket, WebSocketCloseStatus.EndpointUnavailable, e.Message);
                return;
            }

            // Log this event
            string? ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
            int port = HttpContext.Connection.RemotePort;
            LogInformation($"WebSocket connected from {ipAddress}:{port}");

            // Register this client and keep it up-to-date
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_applicationLifetime.ApplicationStopping);
            try
            {
                // Fetch full model copy and send it over initially
                await using (MemoryStream json = await subscribeConnection.GetSerializedObjectModel())
                {
                    await webSocket.SendAsync(json.ToArray(), WebSocketMessageType.Text, true, default);
                }

                // Deal with this connection in full-duplex mode
                AsyncAutoResetEvent dataAcknowledged = new();
                Task rxTask = ReadFromClient(webSocket, dataAcknowledged, cts.Token);
                Task txTask = WriteToClient(webSocket, subscribeConnection, dataAcknowledged, cts.Token);

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
                    LogError("DCS has been stopped");
                    await CloseConnection(webSocket, WebSocketCloseStatus.EndpointUnavailable, "DCS has been stopped");
                }
                else if (e is OperationCanceledException)
                {
                    await CloseConnection(webSocket, WebSocketCloseStatus.EndpointUnavailable, "DWS is shutting down");
                }
                else
                {
                    LogError(e, $"Connection from {ipAddress}:{port} terminated with an exception");
                    await CloseConnection(webSocket, WebSocketCloseStatus.InternalServerError, e.Message);
                }
            }
            finally
            {
                cts.Cancel();
                LogInformation($"WebSocket disconnected from {ipAddress}:{port}");
            }
        }

        /// <summary>
        /// Keep reading from the client
        /// </summary>
        /// <param name="webSocket">WebSocket to read from</param>
        /// <param name="dataAcknowledged">Event to trigger when the client has acknowledged data</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Asynchronous task</returns>
        private async Task ReadFromClient(WebSocket webSocket, AsyncAutoResetEvent dataAcknowledged, CancellationToken cancellationToken)
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
                        await webSocket.SendAsync(PONG, WebSocketMessageType.Text, true, cancellationToken);
                    }
                    else if (!string.IsNullOrWhiteSpace(line))
                    {
                        LogWarning($"Received unsupported line from WebSocket: '{line}'");
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
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Asynchronous task</returns>
        private static async Task WriteToClient(WebSocket webSocket, SubscribeConnection subscribeConnection, AsyncAutoResetEvent dataAcknowledged, CancellationToken cancellationToken)
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
                await using MemoryStream objectModelPatch = await subscribeConnection.GetSerializedObjectModel(cancellationToken);
                await webSocket.SendAsync(objectModelPatch.ToArray(), WebSocketMessageType.Text, true, cancellationToken);
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
}
