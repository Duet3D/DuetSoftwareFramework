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
using DuetAPIClient.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

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
            using CommandConnection cmdConnection = new CommandConnection();

            // 1. Authentification. This will require an extra API command
            // TODO

            // 2. Connect to DCS
            using SubscribeConnection subscription = new SubscribeConnection();
            try
            {
                // Subscribe to object model updates
                await subscription.Connect(SubscriptionMode.Patch, null, socketPath, Program.CancelSource.Token);
            }
            catch (AggregateException ae) when (ae.InnerException is IncompatibleVersionException)
            {
                _logger.LogError($"[{nameof(WebSocketController)}] Incompatible DCS version");
                await CloseConnection(webSocket, WebSocketCloseStatus.InternalServerError, "Incompatible DCS version");
                return;
            }
            catch (SocketException)
            {
                _logger.LogError($"[{nameof(WebSocketController)}] DCS is unavailable");
                await CloseConnection(webSocket, WebSocketCloseStatus.EndpointUnavailable, "DCS is unavailable");
                return;
            }

            // 3. Register this client and keep it up-to-date
            int sessionId = -1;
            try
            {
                _logger.LogInformation("WebSocket connected");

                // 3a. Register this user session. Once authentification has been implemented, the access level may vary
                await cmdConnection.Connect(socketPath, Program.CancelSource.Token);
                string ipAddress = HttpContext.Connection.RemoteIpAddress.ToString();
                int port = HttpContext.Connection.RemotePort;
                sessionId = await cmdConnection.AddUserSession(AccessLevel.ReadWrite, SessionType.HTTP, ipAddress, port, Program.CancelSource.Token);

                // 3b. Fetch full model copy and send it over initially
                using (MemoryStream json = await subscription.GetSerializedMachineModel(Program.CancelSource.Token))
                {
                    await webSocket.SendAsync(json.ToArray(), WebSocketMessageType.Text, true, Program.CancelSource.Token);
                }

                // 3c. Keep sending updates to the client and wait for "OK" after each update
                do
                {
                    // 3d. Wait for response from the client
                    byte[] receivedBytes = new byte[8];
                    WebSocketReceiveResult result = await webSocket.ReceiveAsync(receivedBytes, Program.CancelSource.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // Remote end is closing this connection
                        break;
                    }
                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        // Terminate the connection if binary content is received
                        await CloseConnection(webSocket, WebSocketCloseStatus.InvalidMessageType, "Only text commands are supported");
                        break;
                    }
                    string receivedData = Encoding.UTF8.GetString(receivedBytes);

                    // 3e. Deal with incoming requests
                    if (receivedData.Equals("PING\n", StringComparison.InvariantCultureIgnoreCase))
                    {
                        // Send PONG back to the client
                        await webSocket.SendAsync(PONG, WebSocketMessageType.Text, true, Program.CancelSource.Token);
                        continue;
                    }
                    else if (!receivedData.Equals("OK\n", StringComparison.InvariantCultureIgnoreCase))
                    {
                        // OK is supported to acknowledge the receipt of an object model (patch).
                        // Terminate the connection if anything else is received
                        await CloseConnection(webSocket, WebSocketCloseStatus.ProtocolError, "Invalid command");
                        break;
                    }

                    // 3f. Check for another update and send it to the client but wait for updates only for a limited period of time
                    try
                    {
                        using CancellationTokenSource timeoutCts = new CancellationTokenSource(_configuration.GetValue("ObjectModelUpdateTimeout", 2000));
                        using CancellationTokenSource combinedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, Program.CancelSource.Token);

                        using MemoryStream json = await subscription.GetSerializedMachineModel(combinedCts.Token);
                        await webSocket.SendAsync(json.ToArray(), WebSocketMessageType.Text, true, Program.CancelSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // expected
                    }
                } while (!Program.CancelSource.IsCancellationRequested && webSocket.State == WebSocketState.Open);
            }
            catch (Exception e)
            {
                if (e is OperationCanceledException)
                {
                    await CloseConnection(webSocket, WebSocketCloseStatus.NormalClosure, "DuetWebServer shutting down");
                }
                else
                {
                    _logger.LogError(e, "WebSocket terminated with an exception");
                    await CloseConnection(webSocket, WebSocketCloseStatus.InternalServerError, e.Message);
                }
            }
            finally
            {
                _logger.LogInformation("WebSocket disconnected");

                try
                {
                    // Try to remove this user session again
                    await cmdConnection.RemoveUserSession(sessionId);
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
                // Do not use the main cancellation token here because this may be used when the application is being shut down
                await webSocket.CloseAsync(status, message, default);
            }
        }
    }
}
