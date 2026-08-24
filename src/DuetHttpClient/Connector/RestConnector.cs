using DuetAPI.ObjectModel;
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

namespace DuetHttpClient.Connector;

/// <summary>
/// HTTP connector for SBC mode (which has a RESTful API)
/// </summary>
internal class RestConnector : BaseConnector
{
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
        using HttpClient client = new() { Timeout = options.Timeout };
        using HttpResponseMessage response = await client.GetAsync(new Uri(baseUri, $"machine/connect?password={HttpUtility.UrlEncode(options.Password)}&time={DateTime.Now:s}"), cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
#if NET6_0_OR_GREATER
            using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
            using Stream responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
            Responses.RestConnectResponse responseObj = (await JsonSerializer.DeserializeAsync(responseStream, JsonContext.Default.RestConnectResponse, cancellationToken).ConfigureAwait(false))!;
            return new RestConnector(baseUri, options, responseObj.SessionKey);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            // Invalid password specified
            throw new InvalidPasswordException();
        }

        // Unknown response
        throw new HttpRequestException($"Server returned {response.StatusCode} {response.ReasonPhrase}");
    }

    /// <summary>
    /// Constructor of this class
    /// </summary>
    /// <param name="baseUri">Base URI of the remote board</param>
    /// <param name="options">Connection options or null</param>
    /// <param name="sessionKey">Session key</param>
    private RestConnector(Uri baseUri, DuetHttpOptions options, string sessionKey) : base(baseUri, options)
    {
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
    private volatile string? _sessionKey;

    /// <inheritdoc />
    protected override ValueTask<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken cancellationToken = default, HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        // Set the session key per request. HttpClient.DefaultRequestHeaders is not thread-safe and
        // must not be modified by ReconnectAsync while other requests are in flight
        string? sessionKey = _sessionKey;
        if (sessionKey is not null)
        {
            request.Headers.Add("X-Session-Key", sessionKey);
        }
        return base.SendRequestAsync(request, timeout, cancellationToken, completionOption);
    }

    /// <inheritdoc />
    protected override async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        _sessionKey = null;

        using CancellationTokenSource connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _terminateSession.Token);
        connectCts.CancelAfter(Options.Timeout);

        using HttpResponseMessage response = await HttpClient.GetAsync($"machine/connect?password={HttpUtility.UrlEncode(Options.Password)}", connectCts.Token).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
#if NET6_0_OR_GREATER
            using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
            using Stream responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
            Responses.RestConnectResponse responseObj = (await JsonSerializer.DeserializeAsync(responseStream, JsonContext.Default.RestConnectResponse, cancellationToken).ConfigureAwait(false))!;

            _sessionKey = responseObj.SessionKey;
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
    private readonly List<TaskCompletionSource<object?>> _modelUpdateTCS = [];

    /// <inheritdoc />
    public override async Task WaitForModelUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (disposed)
        {
            throw new ObjectDisposedException(null);
        }
        if (!Options.ObserveObjectModel)
        {
            throw new InvalidOperationException("Cannot wait for object model, because the object model is not observed");
        }

        TaskCompletionSource<object?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_terminateSession.Token, cancellationToken);
        using CancellationTokenRegistration ctsRegistration = cts.Token.Register(() => tcs.TrySetCanceled());
        lock (_modelUpdateTCS)
        {
            _modelUpdateTCS.Add(tcs);
        }

        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_modelUpdateTCS)
            {
                _modelUpdateTCS.Remove(tcs);
            }
        }
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
                using ClientWebSocket webSocket = new();
                webSocket.Options.KeepAliveInterval = Options.KeepAliveInterval;

                string wsScheme = (HttpClient.BaseAddress?.Scheme == "https") ? "wss" : "ws";
                Uri wsUri = new($"{wsScheme}://{HttpClient.BaseAddress?.Host}:{HttpClient.BaseAddress?.Port}/machine?sessionKey={HttpUtility.UrlEncode(_sessionKey)}");

                try
                {
                    await webSocket.ConnectAsync(wsUri, _terminateSession.Token).ConfigureAwait(false);

                    // Read the full object model first
                    using (MemoryStream modelStream = new())
                    {
                        byte[] modelChunk = new byte[8192];
                        do
                        {
                            WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(modelChunk), _terminateSession.Token).ConfigureAwait(false);
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
                        using JsonDocument modelJson = await JsonDocument.ParseAsync(modelStream, cancellationToken: _terminateSession.Token).ConfigureAwait(false);
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

                    // Keep processing further patches
                    do
                    {
                        // Send back the OK response
                        await webSocket.SendAsync(new ArraySegment<byte>(okResponse), WebSocketMessageType.Text, true, _terminateSession.Token).ConfigureAwait(false);

                        // Wait a moment
                        await Task.Delay(Options.UpdateDelay, _terminateSession.Token).ConfigureAwait(false);

                        // Either read a JSON patch or keep the connection alive
                        using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_terminateSession.Token))
                        {
                            cts.CancelAfter(Options.PingInterval);

                            try
                            {
                                using MemoryStream patchStream = new();
                                byte[] patchChunk = new byte[8192];

                                do
                                {
                                    WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(patchChunk), cts.Token).ConfigureAwait(false);
                                    if (result.MessageType == WebSocketMessageType.Close)
                                    {
                                        // Server has closed the connection
                                        break;
                                    }

                                    if (result.Count == pongResponse.Length && patchChunk.AsSpan(0, result.Count).SequenceEqual(pongResponse))
                                    {
                                        // Got a PONG response back
                                        continue;
                                    }

                                    patchStream.Write(patchChunk, 0, result.Count);
                                    if (result.EndOfMessage)
                                    {
                                        // JSON patch is complete
                                        patchStream.Seek(0, SeekOrigin.Begin);
                                        using JsonDocument modelJson = await JsonDocument.ParseAsync(patchStream, cancellationToken: _terminateSession.Token).ConfigureAwait(false);
                                        lock (Model)
                                        {
                                            Model.UpdateFromJson(modelJson.RootElement, false);
                                            if (!Options.ObserveMessages && Model.Messages.Count > 0)
                                            {
                                                // Clear messages automatically if they are not cleared by a consumer
                                                Model.Messages.Clear();
                                            }
                                        }
                                        break;
                                    }
                                } while (true);
                            }
                            catch (OperationCanceledException) when (!_terminateSession.IsCancellationRequested)
                            {
                                // Timeout while waiting for model update, send a PING request
                                await webSocket.SendAsync(new ArraySegment<byte>(pingRequest), WebSocketMessageType.Text, true, _terminateSession.Token).ConfigureAwait(false);
                            }
                        }

                        // Object model is up-to-date
                        lock (_modelUpdateTCS)
                        {
                            foreach (TaskCompletionSource<object?> tcs in _modelUpdateTCS)
                            {
                                tcs.TrySetResult(null);
                            }
                            _modelUpdateTCS.Clear();
                        }
                    }
                    while (webSocket.State == WebSocketState.Open);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Something went wrong, the remote end is offline or unavailable
                    lock (Model)
                    {
                        Model.State.Status = MachineStatus.Disconnected;
                        Model.Global.Clear();
                    }
                    lock (this)
                    {
                        LastConnectionError = e;
                    }
                }

                // Connection lost, check if we can reconnect after a short delay
                try
                {
                    await Task.Delay(Options.RetryDelay, _terminateSession.Token).ConfigureAwait(false);
                    await ReconnectAsync().ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException || !_terminateSession.IsCancellationRequested)
                {
                    // Expected when the remote end is still offline or unavailable. Other errors
                    // (e.g. a changed password) are recorded so the session task keeps retrying
                    lock (this)
                    {
                        LastConnectionError = e;
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
                    using (HttpRequestMessage request = new(HttpMethod.Get, "machine/noop"))
                    {
                        using HttpResponseMessage response = await SendRequestAsync(request, Options.Timeout).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                    }

                    // Wait a moment
                    await Task.Delay(Options.SessionKeepAliveInterval, _terminateSession.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException || !_terminateSession.IsCancellationRequested)
                {
                    // Something went wrong (including request timeouts), try again after the retry delay
                }

                if (!_terminateSession.IsCancellationRequested)
                {
                    // Wait a moment before attempting to reconnect
                    await Task.Delay(Options.RetryDelay, _terminateSession.Token).ConfigureAwait(false);
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
        await _sessionTaskTerminated.Task.ConfigureAwait(false);

        // Disconnect if possible
        if (_sessionKey is not null)
        {
            try
            {
                using CancellationTokenSource cts = new(Options.Timeout);
                using HttpRequestMessage request = new(HttpMethod.Get, "machine/disconnect");
                request.Headers.Add("X-Session-Key", _sessionKey);
                await HttpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }

        // Dispose of the HTTP client
        HttpClient.Dispose();
    }

    /// <inheritdoc />
    public override async Task<string> SendCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        return await SendCodeAsync(code, false, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<string> SendCodeAsync(string code, bool executeAsynchronously, CancellationToken cancellationToken = default)
    {
        string errorMessage = "Invalid number of maximum retries configured";
        for (int i = 0; i <= Options.MaxRetries; i++)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, executeAsynchronously ? "machine/code?async=true" : "machine/code");
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(code));

            using HttpResponseMessage response = await SendRequestAsync(request, Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
#if NET6_0_OR_GREATER
                byte[] responseData = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
#else
                byte[] responseData = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
#endif
                return Encoding.UTF8.GetString(responseData);
            }

            errorMessage = $"Server returned HTTP {response.StatusCode} {response.ReasonPhrase}";
            if (response.StatusCode >= HttpStatusCode.InternalServerError)
            {
                break;
            }
        }
        throw new HttpRequestException(errorMessage);
    }

    /// <summary>
    /// Encode a virtual path for use as a catch-all route value
    /// </summary>
    /// <param name="path">Virtual path to encode</param>
    /// <returns>Encoded path</returns>
    /// <remarks>
    /// The whole path becomes a single route value, so the separators are sent percent-encoded like DWC does it.
    /// ASP.NET decodes route values but leaves %2F alone, and DuetWebServer restores the separators from that
    /// </remarks>
    private static string EncodePath(string path) => Uri.EscapeDataString(path);

    /// <inheritdoc />
    public override async Task UploadAsync(string filename, Stream content, DateTime? lastModified = null, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Put, $"machine/file/{EncodePath(filename)}");
        request.Content = new StreamContent(content);

        using HttpResponseMessage response = await SendRequestAsync(request, Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc />
    public override async Task DeleteAsync(string filename, CancellationToken cancellationToken = default)
    {
        string errorMessage = "Invalid number of maximum retries configured";
        for (int i = 0; i <= Options.MaxRetries; i++)
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Delete, $"machine/file/{EncodePath(filename)}");
                using HttpResponseMessage response = await SendRequestAsync(request, Options.Timeout, cancellationToken).ConfigureAwait(false);
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

    /// <inheritdoc />
    public override async Task MoveAsync(string from, string to, bool force = false, CancellationToken cancellationToken = default)
    {
        string errorMessage = "Invalid number of maximum retries configured";
        for (int i = 0; i <= Options.MaxRetries; i++)
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Post, $"machine/file/move");
                using MultipartFormDataContent formData = new()
                    {
                        { new StringContent(from), "from" },
                        { new StringContent(to), "to" },
                        { new StringContent(force ? "true" : "false"), "force" }
                    };
                request.Content = formData;

                using HttpResponseMessage response = await SendRequestAsync(request, Options.Timeout, cancellationToken).ConfigureAwait(false);
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

    /// <inheritdoc />
    public override async Task MakeDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        string errorMessage = "Invalid number of maximum retries configured";
        for (int i = 0; i <= Options.MaxRetries; i++)
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Put, $"machine/directory/{EncodePath(directory)}");
                using HttpResponseMessage response = await SendRequestAsync(request, Options.Timeout, cancellationToken).ConfigureAwait(false);
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
    public override async Task<HttpResponseMessage> DownloadAsync(string filename, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"machine/file/{EncodePath(filename)}");
        HttpResponseMessage response = await SendRequestAsync(request, Timeout.InfiniteTimeSpan, cancellationToken, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException();
        }

        response.EnsureSuccessStatusCode();
        return response;
    }

    /// <inheritdoc />
    public override async Task<IList<FileListItem>> GetFileListAsync(string directory, CancellationToken cancellationToken = default)
    {
        string errorMessage = "Invalid number of maximum retries configured";
        for (int i = 0; i <= Options.MaxRetries; i++)
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, $"machine/directory/{EncodePath(directory)}");
                using HttpResponseMessage response = await SendRequestAsync(request, Options.Timeout, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
#if NET6_0_OR_GREATER
                    using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
                    using Stream responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
                    return (await JsonSerializer.DeserializeAsync(responseStream, JsonContext.Default.FileNodeArray, cancellationToken).ConfigureAwait(false))!
                        .Select(item => new FileListItem()
                        {
                            Filename = item.Name,
                            IsDirectory = item.Type == 'd',
                            LastModified = item.Date,
                            Size = item.Size
                        })
                        .ToList();
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

    /// <inheritdoc />
    public override async Task<GCodeFileInfo> GetFileInfoAsync(string filename, bool readThumbnailContent, CancellationToken cancellationToken = default)
    {
        string errorMessage = "Invalid number of maximum retries configured";
        for (int i = 0; i <= Options.MaxRetries; i++)
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, $"machine/fileinfo/{EncodePath(filename)}?readThumbnailContent={(readThumbnailContent ? "true" : "false")}");
                using HttpResponseMessage response = await SendRequestAsync(request, Options.Timeout, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
#if NET6_0_OR_GREATER
                    byte[] responseData = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
#else
                    byte[] responseData = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
#endif

                    GCodeFileInfo Deserialize()
                    {
                        Utf8JsonReader reader = new(responseData);
                        GCodeFileInfo fileInfo = new();
                        fileInfo.UpdateFromJsonReader(ref reader, false);
                        return fileInfo;
                    }
                    return Deserialize();
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
