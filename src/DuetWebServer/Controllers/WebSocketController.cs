using System;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Connection;
using DuetAPI.Machine;
using DuetAPIClient;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
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
        /// Configuration of the application
        /// </summary>
        private readonly IConfiguration _configuration;

        /// <summary>
        /// Logger instance
        /// </summary>
        private readonly ILogger _logger;

        /// <summary>
        /// Constructor of a new WebSocket controller
        /// </summary>
        /// <param name="configuration">Configuration of this application</param>
        /// <param name="logger">Logger instance</param>
        public WebSocketController(IConfiguration configuration, ILogger<WebSocketController> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// WS /machine
        /// Provide WebSocket for continuous model updates. This is primarily used to keep DWC2 up-to-date
        /// </summary>
        /// <returns>WebSocket, HTTP status code: (400) Bad request</returns>
        [HttpGet]
        public async Task Get()
        {
            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                using WebSocket webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
                await Process(webSocket);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
            }
        }

        /// <summary>
        /// Deal with a newly opened WebSocket.
        /// A client may receive one of the WS codes: (1001) Endpoint unavailable (1003) Invalid command (1011) Internal error
        /// </summary>
        /// <param name="webSocket">WebSocket connection</param>
        /// <returns>Asynchronous task</returns>
        public async Task Process(WebSocket webSocket)
        {
            string socketPath = _configuration.GetValue("SocketPath", Defaults.FullSocketPath);

            // 1. Authentification. This will require an extra API command
            using CommandConnection commandConnection = new CommandConnection();
            // TODO

            // 2. Connect to DCS
            using SubscribeConnection subscribeConnection = new SubscribeConnection();
            try
            {
                // Subscribe to object model updates
                await subscribeConnection.Connect(SubscriptionMode.Patch, null, socketPath);
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException;
                }
                if (e is IncompatibleVersionException)
                {
                    _logger.LogError($"[{nameof(WebSocketController)}] Incompatible DCS version");
                    await CloseConnection(webSocket, WebSocketCloseStatus.InternalServerError, "Incompatible DCS version");
                    return;
                }
                if (e is SocketException)
                {
                    _logger.LogError($"[{nameof(WebSocketController)}] DCS is not started");
                    await CloseConnection(webSocket, WebSocketCloseStatus.EndpointUnavailable, "DCS is not started");
                    return;
                }
                _logger.LogError(e, $"[{nameof(WebSocketController)}] Failed to connect to DCS");
                await CloseConnection(webSocket, WebSocketCloseStatus.EndpointUnavailable, e.Message);
                return;
            }

            // 3. Log this event
            string ipAddress = HttpContext.Connection.RemoteIpAddress.ToString();
            int port = HttpContext.Connection.RemotePort;
            _logger.LogInformation("WebSocket connected from {0}:{1}", ipAddress, port);

            // 4. Register this client and keep it up-to-date
            using CancellationTokenSource cts = new CancellationTokenSource();
            int sessionId = -1;
            try
            {
                // 4a. Register this user session. Once authentification has been implemented, the access level may vary
                await commandConnection.Connect(socketPath);
                sessionId = await commandConnection.AddUserSession(AccessLevel.ReadWrite, SessionType.HTTP, ipAddress, port);

                // 4b. Fetch full model copy and send it over initially
                using (MemoryStream json = await subscribeConnection.GetSerializedMachineModel())
                {
                    await webSocket.SendAsync(json.ToArray(), WebSocketMessageType.Text, true, default);
                }

                // 4c. Deal with this connection in full-duplex mode
                AsyncAutoResetEvent dataAcknowledged = new AsyncAutoResetEvent();
                Task rxTask = ReadFromClient(webSocket, dataAcknowledged, cts.Token);
                Task txTask = WriteToClient(webSocket, subscribeConnection, dataAcknowledged, cts.Token);

                // 4d. Deal with the tasks' lifecycles
                Task terminatedTask = await Task.WhenAny(rxTask, txTask);
                if (terminatedTask.IsFaulted)
                {
                    throw terminatedTask.Exception;
                }
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    e = ae.InnerException;
                }
                if (e is SocketException)
                {
                    _logger.LogError($"[{nameof(WebSocketController)}] DCS has been stopped");
                    await CloseConnection(webSocket, WebSocketCloseStatus.EndpointUnavailable, "DCS has been stopped");
                }
                else if (e is OperationCanceledException)
                {
                    await CloseConnection(webSocket, WebSocketCloseStatus.EndpointUnavailable, "DWS is shutting down");
                }
                else
                {
                    _logger.LogError(e, $"[{nameof(WebSocketController)}] Connection from {ipAddress}:{port} terminated with an exception");
                    await CloseConnection(webSocket, WebSocketCloseStatus.InternalServerError, e.Message);
                }
            }
            finally
            {
                cts.Cancel();
                _logger.LogInformation("WebSocket disconnected from {0}:{1}", ipAddress, port);
                try
                {
                    // Try to remove this user session again
                    await commandConnection.RemoveUserSession(sessionId);
                }
                catch (Exception e)
                {
                    if (!(e is SocketException))
                    {
                        _logger.LogError(e, "Failed to unregister user session");
                    }
                }
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
                        _logger.LogWarning("Received unsupported line from WebSocket: '{0}'", line);
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
        private async Task WriteToClient(WebSocket webSocket, SubscribeConnection subscribeConnection, AsyncAutoResetEvent dataAcknowledged, CancellationToken cancellationToken)
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
                using MemoryStream objectModelPatch = await subscribeConnection.GetSerializedMachineModel(cancellationToken);
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
        private async Task CloseConnection(WebSocket webSocket, WebSocketCloseStatus status, string message)
        {
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.CloseAsync(status, message, default);
            }
        }
    }
}
