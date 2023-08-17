using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetHttpClient.Exceptions;
using DuetHttpClient.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace DuetHttpClient.Connector
{
    /// <summary>
    /// HTTP connector for SBC mode (which has a RESTful API)
    /// </summary>
    internal class RestConnector : BaseConnector
    {
        /// <summary>
        /// Response from a connect request
        /// </summary>
        private class ConnectResponse
        {
            /// <summary>
            /// Session key
            /// </summary>
            public string SessionKey { get; set; }
        }

        /// <summary>
        /// Establish a HTTP connection to a Duet board running in SBC mode
        /// </summary>
        /// <param name="baseUri">Base URI for the remote board</param>
        /// <param name="options">Default connection options</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>REST connector instance</returns>
        /// <exception cref="HttpRequestException">Board did not return a valid HTTP code</exception>
        /// <exception cref="InvalidPasswordException">Invalid password specified</exception>
        /// <exception cref="NoFreeSessionException">No free session available</exception>
        /// <exception cref="InvalidVersionException">Unsupported DSF version</exception>
        public static async Task<RestConnector> ConnectAsync(Uri baseUri, DuetHttpOptions options, CancellationToken cancellationToken)
        {
            using (HttpClient client = new HttpClient() { Timeout = options.Timeout })
            {
                using (HttpResponseMessage response = await client.GetAsync(new Uri(baseUri, $"machine/connect?password={HttpUtility.UrlPathEncode(options.Password)}&time={DateTime.Now:s}"), cancellationToken))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        using (Stream responseStream = await response.Content.ReadAsStreamAsync())
                        {
                            ConnectResponse responseObj = await JsonSerializer.DeserializeAsync<ConnectResponse>(responseStream, JsonHelper.DefaultJsonOptions, cancellationToken);
                            return new RestConnector(baseUri, options, responseObj.SessionKey);
                        }
                    }

                    if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        // Invalid password specified
                        throw new InvalidPasswordException();
                    }

                    // Unknown response
                    throw new HttpRequestException($"Server returned {response.StatusCode} {response.ReasonPhrase}");
                }
            }
        }

        /// <summary>
        /// Constructor of this class
        /// </summary>
        /// <param name="baseUri">Base URI of the remote board</param>
        /// <param name="options">Connection options or null</param>
        /// <param name="sessionKey">Session key</param>
        private RestConnector(Uri baseUri, DuetHttpOptions options, string sessionKey) : base(baseUri, options)
        {
            HttpClient.DefaultRequestHeaders.Add("X-Session-Key", sessionKey);
            _sessionKey = sessionKey;

            if (options.ObserveMessages || options.ObserveObjectModel)
            {
                // Open WebSocket to keep receiving object model updates
                _ = Task.Run(ReceiveObjectModel);
            }
            else
            {
                // Make new task to request /machine/noop in regular intervals
                _ = Task.Run(MaintainSession);
            }
        }

        /// <summary>
        /// Session key of the underlying HTTP session
        /// </summary>
        private string _sessionKey;

        /// <summary>
        /// Reconnect to the board when the connection has been reset
        /// </summary>
        protected override async Task Reconnect(CancellationToken cancellationToken = default)
        {
            _sessionKey = null;
            HttpClient.DefaultRequestHeaders.Remove("X-Session-Key");

            using (CancellationTokenSource connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _terminateSession.Token))
            {
                connectCts.CancelAfter(Options.Timeout);

                using (HttpResponseMessage response = await HttpClient.GetAsync($"machine/connect?password={Options.Password}", connectCts.Token))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        using (Stream responseStream = await response.Content.ReadAsStreamAsync())
                        {
                            ConnectResponse responseObj = await JsonSerializer.DeserializeAsync<ConnectResponse>(responseStream, JsonHelper.DefaultJsonOptions, cancellationToken);

                            _sessionKey = responseObj.SessionKey;
                            HttpClient.DefaultRequestHeaders.Add("X-Session-Key", responseObj.SessionKey);
                        }
                    }
                    else if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        // Invalid password specified
                        throw new InvalidPasswordException();
                    }
                    else
                    {
                        // Unknown response
                        throw new HttpRequestException($"Server returned {response.StatusCode} {response.ReasonPhrase}");
                    }
                }
            }
        }

        /// <summary>
        /// WebSocket response to send after receiving JSON data
        /// </summary>
        private static readonly byte[] okResponse = Encoding.UTF8.GetBytes("OK\n");

        /// <summary>
        /// PING request from the client
        /// </summary>
        private static readonly byte[] pingRequest = Encoding.UTF8.GetBytes("PING\n");

        /// <summary>
        /// PONG response from the server
        /// </summary>
        private static readonly byte[] pongResponse = Encoding.UTF8.GetBytes("PONG\n");

        /// <summary>
        /// TCS to complete when the object model is up-to-date
        /// </summary>
        private readonly List<TaskCompletionSource<object>> _modelUpdateTCS = new List<TaskCompletionSource<object>>();

        /// <summary>
        /// Wait for the object model to be up-to-date
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public override Task WaitForModelUpdate(CancellationToken cancellationToken = default)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(null);
            }
            if (!Options.ObserveObjectModel)
            {
                throw new InvalidOperationException("Cannot wait for object model, because the object model is not observed");
            }

            TaskCompletionSource<object> tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_terminateSession.Token, cancellationToken);
            CancellationTokenRegistration ctsRegistration = cts.Token.Register(() => tcs.TrySetCanceled());
            lock (_modelUpdateTCS)
            {
                _modelUpdateTCS.Add(tcs);
            }

            return tcs.Task.ContinueWith(async task =>
            {
                try
                {
                    await task;
                }
                finally
                {
                    ctsRegistration.Dispose();
                }
            }, TaskContinuationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// Keep receiving object model updates
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private async Task ReceiveObjectModel()
        {
            try
            {
                do
                {
                    using (ClientWebSocket webSocket = new ClientWebSocket())
                    {
                        webSocket.Options.KeepAliveInterval = Options.KeepAliveInterval;

                        string wsScheme = (HttpClient.BaseAddress.Scheme == "https") ? "wss" : "ws";
                        Uri wsUri = new Uri($"{wsScheme}://{HttpClient.BaseAddress.Host}:{HttpClient.BaseAddress.Port}/machine?sessionKey={HttpUtility.UrlPathEncode(_sessionKey)}");

                        try
                        {
                            await webSocket.ConnectAsync(wsUri, _terminateSession.Token);

                            // Read the full object model first
                            using (MemoryStream modelStream = new MemoryStream())
                            {
                                byte[] modelChunk = new byte[8192];
                                do
                                {
                                    WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(modelChunk), _terminateSession.Token);
                                    if (result.MessageType == WebSocketMessageType.Close)
                                    {
                                        // Server has closed the connection
                                        break;
                                    }

                                    modelStream.Write(modelChunk, 0, result.Count);
                                    if (result.EndOfMessage)
                                    {
                                        break;
                                    }
                                } while (true);

                                modelStream.Seek(0, SeekOrigin.Begin);
                                using (JsonDocument modelJson = await JsonDocument.ParseAsync(modelStream, cancellationToken: _terminateSession.Token))
                                {
                                    lock (Model)
                                    {
                                        Model.UpdateFromJson(modelJson.RootElement, false);
                                        if (!Options.ObserveMessages && Model.Messages.Count > 0)
                                        {
                                            // Clear messages automatically if they are not cleared by a consumer
                                            Model.Messages.Clear();
                                        }
                                    }
                                }
                            }

                            // Keep processing further patches
                            do
                            {
                                // Send back the OK response
                                await webSocket.SendAsync(new ArraySegment<byte>(okResponse), WebSocketMessageType.Text, true, _terminateSession.Token);

                                // Wait a moment
                                await Task.Delay(Options.UpdateDelay, _terminateSession.Token);

                                // Either read a JSON patch or keep the connection alive
                                using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_terminateSession.Token))
                                {
                                    cts.CancelAfter(Options.PingInterval);

                                    try
                                    {
                                        using (MemoryStream patchStream = new MemoryStream())
                                        {
                                            byte[] patchChunk = new byte[8192];

                                            do
                                            {
                                                WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(patchChunk), _terminateSession.Token);
                                                if (result.MessageType == WebSocketMessageType.Close)
                                                {
                                                    // Server has closed the connection
                                                    break;
                                                }

                                                if (result.Count == pongResponse.Length && patchChunk.SequenceEqual(pongResponse))
                                                {
                                                    // Got a PONG response back
                                                    continue;
                                                }

                                                patchStream.Write(patchChunk, 0, result.Count);
                                                if (result.EndOfMessage)
                                                {
                                                    // JSON patch is complete
                                                    patchStream.Seek(0, SeekOrigin.Begin);
                                                    using (JsonDocument modelJson = await JsonDocument.ParseAsync(patchStream, cancellationToken: _terminateSession.Token))
                                                    {
                                                        lock (Model)
                                                        {
                                                            Model.UpdateFromJson(modelJson.RootElement, false);
                                                            if (!Options.ObserveMessages && Model.Messages.Count > 0)
                                                            {
                                                                // Clear messages automatically if they are not cleared by a consumer
                                                                Model.Messages.Clear();
                                                            }
                                                        }
                                                    }
                                                    break;
                                                }
                                            } while (true);
                                        }
                                    }
                                    catch (OperationCanceledException) when (!_terminateSession.IsCancellationRequested)
                                    {
                                        // Timeout while waiting for model update, send a PING request
                                        await webSocket.SendAsync(new ArraySegment<byte>(pingRequest), WebSocketMessageType.Text, true, _terminateSession.Token);
                                    }
                                }

                                // Object model is up-to-date
                                lock (_modelUpdateTCS)
                                {
                                    foreach (TaskCompletionSource<object> tcs in _modelUpdateTCS)
                                    {
                                        tcs.TrySetResult(null);
                                    }
                                    _modelUpdateTCS.Clear();
                                }
                            }
                            while (webSocket.State == WebSocketState.Open);
                        }
                        catch (Exception e) when (!(e is OperationCanceledException))
                        {
                            // Something went wrong, the remote end is offline or unavailable
                            lock (Model)
                            {
                                Model.State.Status = MachineStatus.Disconnected;
                                Model.Global.Clear();
                            }
                        }

                        // Connection lost, check if we can reconnect after a short delay
                        try
                        {
                            await Task.Delay(2000, _terminateSession.Token);
                            await Reconnect();
                        }
                        catch (Exception e) when (e is OperationCanceledException || e is HttpRequestException)
                        {
                            // expected when the remote end is still offline or unavailable
                        }
                    }
                }
                while (!_terminateSession.IsCancellationRequested);
            }
            finally
            {
                _sessionTaskTerminated.SetResult(null);
            }
        }

        /// <summary>
        /// Maintain the HTTP session without querying the object model
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private async Task MaintainSession()
        {
            try
            {
                do
                {
                    try
                    {
                        // Perform a NOOP request
                        using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "machine/noop"))
                        {
                            using (HttpResponseMessage response = await SendRequest(request, Options.Timeout))
                            {
                                response.EnsureSuccessStatusCode();
                            }
                        }

                        // Wait a moment
                        await Task.Delay(Options.SessionKeepAliveInterval, _terminateSession.Token);
                    }
                    catch (Exception e) when (!(e is OperationCanceledException))
                    {
                        // Something went wrong
                    }

                    if (!_terminateSession.IsCancellationRequested)
                    {
                        // Wait a moment before attempting to reconnect
                        await Task.Delay(2000);
                    }
                }
                while (!_terminateSession.IsCancellationRequested);
            }
            finally
            {
                _sessionTaskTerminated.SetResult(null);
            }
        }

        /// <summary>
        /// Indicates if this instance has been disposed
        /// </summary>
        private bool disposed;

        /// <summary>
        /// Dispose this instance and the corresponding session
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override async ValueTask DisposeAsync()
        {
            lock (this)
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
            }

            // Terminate the session and wait for it
            _terminateSession.Cancel();
            await _sessionTaskTerminated.Task;

            // Disconnect if possible
            if (_sessionKey != null)
            {
                try
                {
                    using (CancellationTokenSource cts = new CancellationTokenSource(Options.Timeout))
                    {
                        await HttpClient.GetAsync("machine/disconnect", cts.Token);
                    }
                }
                catch
                {
                    // ignored
                }
            }

            // Dispose of the HTTP client
            HttpClient.Dispose();
        }

        /// <summary>
        /// Send a G/M/T-code and return the G-code reply
        /// </summary>
        /// <param name="code">Code to send</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Code reply</returns>
        public override Task<string> SendCode(string code, CancellationToken cancellationToken = default) => SendCode(code, false, cancellationToken);

        /// <summary>
        /// Send a G/M/T-code and return the G-code reply
        /// </summary>
        /// <param name="code">Code to send</param>
        /// <param name="executeAsynchronously">Don't wait for the code to finish</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Code reply</returns>
        public override async Task<string> SendCode(string code, bool executeAsynchronously, CancellationToken cancellationToken = default)
        {
            string errorMessage = "Invalid number of maximum retries configured";
            for (int i = 0; i <= Options.MaxRetries; i++)
            {
                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, executeAsynchronously ? "machine/code?async=true" : "machine/code"))
                {
                    request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(code));

                    using (HttpResponseMessage response = await SendRequest(request, Timeout.InfiniteTimeSpan, cancellationToken))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            byte[] responseData = await response.Content.ReadAsByteArrayAsync();
                            return Encoding.UTF8.GetString(responseData);
                        }

                        errorMessage = $"Server returned HTTP {response.StatusCode} {response.ReasonPhrase}";
                        if (response.StatusCode >= HttpStatusCode.InternalServerError)
                        {
                            break;
                        }
                    }
                }
            }
            throw new HttpRequestException(errorMessage);
        }

        /// <summary>
        /// Upload arbitrary content to a file
        /// </summary>
        /// <param name="filename">Target filename</param>
        /// <param name="content">File content</param>
        /// <param name="lastModified">Last modified datetime. Ignored in SBC mode</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public override async Task Upload(string filename, Stream content, DateTime? lastModified = null, CancellationToken cancellationToken = default)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Put, $"machine/file/{HttpUtility.UrlPathEncode(filename)}"))
            {
                request.Content = new StreamContent(content);

                using (HttpResponseMessage response = await SendRequest(request, Timeout.InfiniteTimeSpan, cancellationToken))
                {
                    response.EnsureSuccessStatusCode();
                }
            }
        }

        /// <summary>
        /// Delete a file or directory
        /// </summary>
        /// <param name="filename">Target filename</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public override async Task Delete(string filename, CancellationToken cancellationToken = default)
        {
            string errorMessage = "Invalid number of maximum retries configured";
            for (int i = 0; i <= Options.MaxRetries; i++)
            {
                try
                {
                    using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Delete, $"machine/file/{HttpUtility.UrlPathEncode(filename)}"))
                    {
                        using (HttpResponseMessage response = await SendRequest(request, Options.Timeout, cancellationToken))
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                return;
                            }

                            if (response.StatusCode == HttpStatusCode.NotFound)
                            {
                                throw new FileNotFoundException();
                            }

                            errorMessage = $"Server returned HTTP {response.StatusCode} {response.ReasonPhrase}";
                            if (response.StatusCode >= HttpStatusCode.InternalServerError)
                            {
                                break;
                            }
                        }
                    }
                }
                catch (OperationCanceledException oce)
                {
                    if (cancellationToken.IsCancellationRequested || _terminateSession.IsCancellationRequested)
                    {
                        throw;
                    }
                    errorMessage = oce.Message;
                }
            }
            throw new HttpRequestException(errorMessage);
        }

        /// <summary>
        /// Move a file or directory
        /// </summary>
        /// <param name="from">Source file</param>
        /// <param name="to">Destination file</param>
        /// <param name="force">Overwrite file if it already exists</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public override async Task Move(string from, string to, bool force = false, CancellationToken cancellationToken = default)
        {
            string errorMessage = "Invalid number of maximum retries configured";
            for (int i = 0; i <= Options.MaxRetries; i++)
            {
                try
                {
                    using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"machine/file/move"))
                    {
                        using (MultipartFormDataContent formData = new MultipartFormDataContent
                        {
                            { new StringContent(from), "from" },
                            { new StringContent(to), "to" },
                            { new StringContent(force ? "true" : "false"), "force" }
                        })
                        {
                            request.Content = formData;

                            using (HttpResponseMessage response = await SendRequest(request, Options.Timeout, cancellationToken))
                            {
                                if (response.IsSuccessStatusCode)
                                {
                                    return;
                                }

                                if (response.StatusCode == HttpStatusCode.NotFound)
                                {
                                    throw new FileNotFoundException();
                                }

                                errorMessage = $"Server returned HTTP {response.StatusCode} {response.ReasonPhrase}";
                                if (response.StatusCode >= HttpStatusCode.InternalServerError)
                                {
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException oce)
                {
                    if (cancellationToken.IsCancellationRequested || _terminateSession.IsCancellationRequested)
                    {
                        throw;
                    }
                    errorMessage = oce.Message;
                }
            }
            throw new HttpRequestException(errorMessage);
        }

        /// <summary>
        /// Make a new directory
        /// </summary>
        /// <param name="directory">Target directory</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public override async Task MakeDirectory(string directory, CancellationToken cancellationToken = default)
        {
            string errorMessage = "Invalid number of maximum retries configured";
            for (int i = 0; i <= Options.MaxRetries; i++)
            {
                try
                {
                    using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Put, $"machine/directory/{HttpUtility.UrlPathEncode(directory)}"))
                    {
                        using (HttpResponseMessage response = await SendRequest(request, Options.Timeout, cancellationToken))
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                return;
                            }

                            errorMessage = $"Server returned HTTP {response.StatusCode} {response.ReasonPhrase}";
                            if (response.StatusCode >= HttpStatusCode.InternalServerError)
                            {
                                break;
                            }
                        }
                    }
                }
                catch (OperationCanceledException oce)
                {
                    if (cancellationToken.IsCancellationRequested || _terminateSession.IsCancellationRequested)
                    {
                        throw;
                    }
                    errorMessage = oce.Message;
                }
            }
            throw new HttpRequestException(errorMessage);
        }

        /// <summary>
        /// Download a file
        /// </summary>
        /// <param name="filename">Name of the file to download</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Disposable download response</returns>
        public override async Task<HttpResponseMessage> Download(string filename, CancellationToken cancellationToken = default)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"machine/file/{HttpUtility.UrlPathEncode(filename)}"))
            {
                HttpResponseMessage response = await SendRequest(request, Timeout.InfiniteTimeSpan, cancellationToken);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new FileNotFoundException();
                }

                response.EnsureSuccessStatusCode();
                return response;
            }
        }

        /// <summary>
        /// Class representing a file item
        /// </summary>
        private class FileNode
        {
            public DateTime Date { get; set; }
            public string Name { get; set; }
            public long Size { get; set; }
            public char Type { get; set; }
        }

        /// <summary>
        /// Enumerate all files and directories in the given directory
        /// </summary>
        /// <param name="directory">Directory to query</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>List of all files and directories</returns>
        public override async Task<IList<FileListItem>> GetFileList(string directory, CancellationToken cancellationToken = default)
        {
            string errorMessage = "Invalid number of maximum retries configured";
            for (int i = 0; i <= Options.MaxRetries; i++)
            {
                try
                {
                    using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"machine/directory/{HttpUtility.UrlPathEncode(directory)}"))
                    {
                        using (HttpResponseMessage response = await SendRequest(request, Options.Timeout, cancellationToken))
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                using (Stream responseStream = await response.Content.ReadAsStreamAsync())
                                {
                                    return (await JsonSerializer.DeserializeAsync<List<FileNode>>(responseStream, JsonHelper.DefaultJsonOptions, cancellationToken))
                                        .Select(item => new FileListItem()
                                        {
                                            Filename = item.Name,
                                            IsDirectory = item.Type == 'd',
                                            LastModified = item.Date,
                                            Size = item.Size
                                        })
                                        .ToList();
                                }
                            }

                            if (response.StatusCode == HttpStatusCode.NotFound)
                            {
                                throw new DirectoryNotFoundException();
                            }

                            errorMessage = $"Server returned HTTP {response.StatusCode} {response.ReasonPhrase}";
                            if (response.StatusCode >= HttpStatusCode.InternalServerError)
                            {
                                break;
                            }
                        }
                    }
                }
                catch (OperationCanceledException oce)
                {
                    if (cancellationToken.IsCancellationRequested || _terminateSession.IsCancellationRequested)
                    {
                        throw;
                    }
                    errorMessage = oce.Message;
                }
            }
            throw new HttpRequestException(errorMessage);
        }

        /// <summary>
        /// Get G-code file info
        /// </summary>
        /// <param name="filename">File to query</param>
        /// <param name="readThumbnailContent">Whether thumbnail contents shall be parsed</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>G-code file info</returns>
        public override async Task<GCodeFileInfo> GetFileInfo(string filename, bool readThumbnailContent, CancellationToken cancellationToken = default)
        {
            string errorMessage = "Invalid number of maximum retries configured";
            for (int i = 0; i <= Options.MaxRetries; i++)
            {
                try
                {
                    using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"machine/fileinfo/{HttpUtility.UrlPathEncode(filename)}?readThumbnailContent={(readThumbnailContent ? "true" : "false")}"))
                    {
                        using (HttpResponseMessage response = await SendRequest(request, Options.Timeout, cancellationToken))
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                Stream responseStream = await response.Content.ReadAsStreamAsync();
                                return await JsonSerializer.DeserializeAsync<GCodeFileInfo>(responseStream, JsonHelper.DefaultJsonOptions, cancellationToken);
                            }

                            if (response.StatusCode == HttpStatusCode.NotFound)
                            {
                                throw new FileNotFoundException();
                            }

                            errorMessage = $"Server returned HTTP {response.StatusCode} {response.ReasonPhrase}";
                            if (response.StatusCode >= HttpStatusCode.InternalServerError)
                            {
                                break;
                            }
                        }
                    }
                }
                catch (OperationCanceledException oce)
                {
                    if (cancellationToken.IsCancellationRequested || _terminateSession.IsCancellationRequested)
                    {
                        throw;
                    }
                    errorMessage = oce.Message;
                }
            }
            throw new HttpRequestException(errorMessage);
        }

        // ** Plugin and system package calls are not supported (yet) **
    }
}
