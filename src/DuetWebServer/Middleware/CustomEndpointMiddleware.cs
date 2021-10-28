using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DuetWebServer.Middleware
{
    /// <summary>
    /// Middleware providing with custom HTTP/WebSocket endpoints
    /// </summary>
    public class CustomEndpointMiddleware
    {
        /// <summary>
        /// Next request delegate to call
        /// </summary>
        private readonly RequestDelegate _next;

        /// <summary>
        /// Configuration of this application
        /// </summary>
        private readonly IConfiguration _configuration;

        /// <summary>
        /// Logger instance
        /// </summary>
        private readonly ILogger _logger;

        /// <summary>
        /// Host application lifetime
        /// </summary>
        private readonly IHostApplicationLifetime _applicationLifetime;

        /// <summary>
        /// Constructor of this middleware
        /// </summary>
        /// <param name="next">Next request delegate</param>
        /// <param name="configuration">Application configuration</param>
        /// <param name="logger">Logger instance</param>
        public CustomEndpointMiddleware(RequestDelegate next, IConfiguration configuration, ILogger<CustomEndpointMiddleware> logger, IHostApplicationLifetime applicationLifetime)
        {
            _next = next;
            _configuration = configuration;
            _logger = logger;
            _applicationLifetime = applicationLifetime;
        }

        /// <summary>
        /// Called when a new HTTP request is received
        /// </summary>
        /// <param name="context">HTTP context</param>
        /// <returns>Asynchronous task</returns>
        public async Task InvokeAsync(HttpContext context)
        {
            // Check if this endpoint is reserved for any route
            HttpEndpoint httpEndpoint = null;
            lock (Services.ModelObserver.Endpoints)
            {
                string method = context.WebSockets.IsWebSocketRequest ? "WebSocket" : context.Request.Method.ToString();
                if (Services.ModelObserver.Endpoints.TryGetValue($"{method}{context.Request.Path.Value}", out HttpEndpoint ep))
                {
                    httpEndpoint = ep;
                }
                else
                {
                    _logger.LogInformation("No endpoint found for {0} request via {1}", method, context.Request.Path.Value);
                }
            }

            if (httpEndpoint != null)
            {
                // Cnnect to the given UNIX socket endpoint
                using HttpEndpointConnection endpointConnection = new();
                endpointConnection.Connect(httpEndpoint.UnixSocket);

                // See what to do with this request
                int sessionId = -1;
                if (httpEndpoint.EndpointType == HttpEndpointType.WebSocket)
                {
                    if (context.Request.Query.TryGetValue("sessionKey", out StringValues sessionKeys))
                    {
                        foreach (string sessionKey in sessionKeys)
                        {
                            sessionId = Authorization.Sessions.GetSessionId(sessionKey);
                            if (sessionId != -1)
                            {
                                break;
                            }
                        }
                    }

                    if (Services.ModelObserver.CheckWebSocketOrigin(context))
                    {
                        using WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync();

                        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_applicationLifetime.ApplicationStopping);
                        Task webSocketTask = ReadFromWebSocket(webSocket, endpointConnection, sessionId, cts.Token);
                        Task unixSocketTask = ReadFromUnixSocket(webSocket, endpointConnection, cts.Token);

                        await Task.WhenAny(webSocketTask, unixSocketTask);
                        cts.Cancel();
                    }
                }
                else
                {
                    if (context.Request.Headers.TryGetValue("X-Session-Key", out StringValues sessionKeys))
                    {
                        foreach (string sessionKey in sessionKeys)
                        {
                            sessionId = Authorization.Sessions.GetSessionId(sessionKey);
                            if (sessionId != -1)
                            {
                                break;
                            }
                        }
                    }

                    await ProcessRestRequst(context, httpEndpoint, endpointConnection, sessionId);
                }
            }
            else
            {
                // Call the next delegate/middleware in the pipeline
                await _next(context);
            }
        }

        /// <summary>
        /// Process incoming data from the WebSocket
        /// </summary>
        /// <param name="webSocket">Remote WebSocket</param>
        /// <param name="endpointConnection">Local UNIX socket connection</param>
        /// <param name="sessionId">Session ID</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Asynchronous task</returns>
        private async Task ReadFromWebSocket(WebSocket webSocket, HttpEndpointConnection endpointConnection, int sessionId, CancellationToken cancellationToken)
        {
            int bufferSize = _configuration.GetValue("WebSocketBufferSize", 8192);
            byte[] rxBuffer = new byte[bufferSize];

            do
            {
                // Read data from the WebSocket
                WebSocketReceiveResult result = await webSocket.ReceiveAsync(rxBuffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Remote end is closing this connection
                    return;
                }
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // Terminate the connection if binary content is received
                    await CloseConnection(webSocket, WebSocketCloseStatus.InvalidMessageType, "Only text commands are supported");
                    break;
                }

                // Forward it to the UNIX socket connection
                ReceivedHttpRequest receivedHttpRequest = new()
                {
                    Body = Encoding.UTF8.GetString(rxBuffer, 0, result.Count),
                    SessionId = sessionId
                };
                await endpointConnection.SendHttpRequest(receivedHttpRequest, cancellationToken);
            }
            while (!cancellationToken.IsCancellationRequested);
        }

        /// <summary>
        /// Process incoming data from the UNIX socket
        /// </summary>
        /// <param name="webSocket">Remote WebSocket</param>
        /// <param name="endpointConnection">Local UNIX socket connection</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Asynchronous task</returns>
        private async Task ReadFromUnixSocket(WebSocket webSocket, HttpEndpointConnection endpointConnection, CancellationToken cancellationToken)
        {
            do
            {
                // Read data from the UNIX socket connection
                SendHttpResponse response = await endpointConnection.GetHttpResponse(cancellationToken);
                if (response.StatusCode >= 1000)
                {
                    _logger.LogDebug("Closing WebSocket with status code {0} ({1})", response.StatusCode, response.Response);
                    await CloseConnection(webSocket, (WebSocketCloseStatus)response.StatusCode, response.Response);
                    break;
                }
                else
                {
                    _logger.LogDebug("Sending WebSocket data");
                    await webSocket.SendAsync(Encoding.UTF8.GetBytes(response.Response), WebSocketMessageType.Text, true, cancellationToken);
                }
            }
            while (!cancellationToken.IsCancellationRequested);
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

        /// <summary>
        /// Process a RESTful HTTP request
        /// </summary>
        /// <param name="context">HTTP context</param>
        /// <param name="endpoint">HTTP endpoint descriptor</param>
        /// <param name="endpointConnection">Endpoint connection</param>
        /// <param name="sessionId">Session ID</param>
        /// <returns>Asynchronous task</returns>
        private static async Task ProcessRestRequst(HttpContext context, HttpEndpoint endpoint, HttpEndpointConnection endpointConnection, int sessionId)
        {
            string body;
            if (endpoint.IsUploadRequest)
            {
                // Write to a temporary file
                string filename = Path.GetTempFileName();
                using (FileStream fileStream = new(filename, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    await context.Request.Body.CopyToAsync(fileStream);
                }

                // Adjust the file permissions if possible
                endpointConnection.GetPeerCredentials(out _, out int uid, out int gid);
                if (uid != 0 && gid != 0)
                {
                    LinuxApi.Commands.Chown(filename, uid, gid);
                }
                body = filename;
            }
            else
            {
                // Read the body content
                using StreamReader reader = new(context.Request.Body);
                body = await reader.ReadToEndAsync();
            }

            // Prepare the HTTP request notification
            ReceivedHttpRequest receivedHttpRequest = new()
            {
                Body = body,
                ContentType = context.Request.ContentType,
                SessionId = sessionId
            };

            foreach (var item in context.Request.Headers)
            {
                receivedHttpRequest.Headers.Add(item.Key, item.Value.ToString());
            }

            foreach (var item in context.Request.Query)
            {
                receivedHttpRequest.Queries.Add(item.Key, item.Value.ToString());
            }

            // Send it to the third-party application and get a response type
            await endpointConnection.SendHttpRequest(receivedHttpRequest);
            SendHttpResponse httpResponse = await endpointConnection.GetHttpResponse();

            // Send the response to the HTTP client
            context.Response.StatusCode = httpResponse.StatusCode;
            if (httpResponse.ResponseType == HttpResponseType.File)
            {
                context.Response.ContentType = "application/octet-stream";

                using FileStream fs = new(httpResponse.Response, FileMode.Open, FileAccess.Read);
                await fs.CopyToAsync(context.Response.Body);
            }
            else
            {
                switch (httpResponse.ResponseType)
                {
                    case HttpResponseType.StatusCode:
                        context.Response.ContentType = null;
                        break;
                    case HttpResponseType.PlainText:
                        context.Response.ContentType = "text/plain;charset=utf-8";
                        break;
                    case HttpResponseType.JSON:
                        context.Response.ContentType = "application/json";
                        break;
                }

                await context.Response.BodyWriter.WriteAsync(Encoding.UTF8.GetBytes(httpResponse.Response));
            }
        }
    }
}
