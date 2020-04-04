﻿using DuetAPI.Commands;
using DuetAPI.Machine;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
        /// Constructor of this middleware
        /// </summary>
        /// <param name="next">Next request delegate</param>
        /// <param name="configuration">Application configuration</param>
        /// <param name="logger">Logger instance</param>
        public CustomEndpointMiddleware(RequestDelegate next, IConfiguration configuration, ILogger<CustomEndpointMiddleware> logger)
        {
            _next = next;
            _configuration = configuration;
            _logger = logger;
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
                // Try to connect to the given UNIX socket
                using HttpEndpointConnection endpointConnection = new HttpEndpointConnection();
                endpointConnection.Connect(httpEndpoint.UnixSocket);

                // Try to find a user session
                int sessionId = -1;
                lock (Services.ModelObserver.UserSessions)
                {
                    string ipAddress = context.Connection.RemoteIpAddress.ToString();
                    if (Services.ModelObserver.UserSessions.TryGetValue(ipAddress, out int foundSessionId))
                    {
                        sessionId = foundSessionId;
                    }
                }

                // See what to do with this request
                if (httpEndpoint.EndpointType == HttpEndpointType.WebSocket)
                {
                    using WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync();

                    using CancellationTokenSource cts = new CancellationTokenSource();
                    Task webSocketTask = ReadFromWebSocket(webSocket, endpointConnection, sessionId, cts.Token);
                    Task unixSocketTask = ReadFromUnixSocket(webSocket, endpointConnection, cts.Token);

                    await Task.WhenAny(webSocketTask, unixSocketTask);
                    cts.Cancel();
                }
                else
                {
                    await ProcessRestRequst(context, endpointConnection, sessionId);
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
                ReceivedHttpRequest receivedHttpRequest = new ReceivedHttpRequest
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
        /// <param name="endpointConnection">Endpoint connection</param>
        /// <param name="sessionId">Session ID</param>
        /// <returns>Asynchronous task</returns>
        private async Task ProcessRestRequst(HttpContext context, HttpEndpointConnection endpointConnection, int sessionId)
        {
            // Deal with RESTful HTTP requests. Read the full HTTP request body first
            string body;
            using (StreamReader reader = new StreamReader(context.Request.Body))
            {
                body = await reader.ReadToEndAsync();
            }

            // Prepare the HTTP request notification
            ReceivedHttpRequest receivedHttpRequest = new ReceivedHttpRequest
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

                using FileStream fs = new FileStream(httpResponse.Response, FileMode.Open, FileAccess.Read);
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
