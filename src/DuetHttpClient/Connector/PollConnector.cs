using DuetAPI.Commands;
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
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace DuetHttpClient.Connector
{
    /// <summary>
    /// HTTP connector for standalone mode (which requires polling)
    /// </summary>
    internal class PollConnector : BaseConnector
    {
        /// <summary>
        /// Minimum HTTP API level to support this
        /// </summary>
        private const int MinApiLevel = 1;

        /// <summary>
        /// Generic reply to report if an error occurred
        /// </summary>
        public class ErrResponse
        {
            public int Err { get; set; }
        }

        /// <summary>
        /// Reply for a rr_connect request
        /// </summary>
        private class ConnectResponse : ErrResponse
        {
            public bool IsEmulated { get; set; }
            public int ApiLevel { get; set; }
        }

        /// <summary>
        /// Establish a HTTP connection to a Duet board running in standalone mode
        /// </summary>
        /// <param name="baseUri">Base URI of the remote board</param>
        /// <param name="options">Default connection options</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Poll connector instance</returns>
        /// <exception cref="HttpRequestException">Board did not return a valid HTTP code</exception>
        /// <exception cref="InvalidPasswordException">Invalid password specified</exception>
        /// <exception cref="NoFreeSessionException">No free session available</exception>
        /// <exception cref="InvalidVersionException">Unsupported DSF version</exception>
        public static async Task<PollConnector> ConnectAsync(Uri baseUri, DuetHttpOptions options, CancellationToken cancellationToken)
        {
            using HttpClient client = new HttpClient()
            {
                Timeout = options.Timeout
            };

            using HttpResponseMessage response = await client.GetAsync(new Uri(baseUri, "rr_connect?password={HttpUtility.UrlPathEncode(password)}&time={DateTime.Now:s}"), cancellationToken);
            response.EnsureSuccessStatusCode();

            await using Stream responseStream = await response.Content.ReadAsStreamAsync();
            ConnectResponse connectResponse = await JsonSerializer.DeserializeAsync<ConnectResponse>(responseStream, JsonHelper.DefaultJsonOptions, cancellationToken);
            switch (connectResponse.Err)
            {
                case 0: break;
                case 1: throw new InvalidPasswordException();
                case 2: throw new NoFreeSessionException();
                default: throw new LoginException($"rr_connect returned unknown err {connectResponse.Err}");
            }

            if (connectResponse.IsEmulated)
            {
                // Don't attempt to use emulated endpoints since the remote server provides support for RESTful calls too
                throw new HttpRequestException("HTTP backend is emulated");
            }

            if (connectResponse.ApiLevel < MinApiLevel)
            {
                throw new InvalidVersionException("Incompatible API level");
            }

            return new PollConnector(baseUri, options);
        }

        /// <summary>
        /// Constructor of a PollConnector instance
        /// </summary>
        /// <param name="baseUri">Base URI of the remote board</param>
        /// <param name="options">Connection options or null</param>
        private PollConnector(Uri baseUri, DuetHttpOptions options) : base(baseUri, options)
        {
            // Make new task to keep the session alive
            _ = Task.Run(MaintainSession);
        }

        /// <summary>
        /// Dictionary of keys vs sequence numbers
        /// </summary>
        private readonly Dictionary<string, int> _seqs = new Dictionary<string, int>();

        /// <summary>
        /// Dictionary of running codes vs sequence numbers
        /// </summary>
        private readonly Dictionary<TaskCompletionSource<string>, int> _runningCodes = new Dictionary<TaskCompletionSource<string>, int>();

        /// <summary>
        /// Reconnect to the board when the connection has been reset
        /// </summary>
        protected override async Task Reconnect(CancellationToken cancellationToken = default)
        {
            lock (_seqs)
            {
                _seqs.Clear();
            }

            lock (_runningCodes)
            {
                foreach (TaskCompletionSource<string> tcs in _runningCodes.Keys)
                {
                    tcs.SetCanceled();
                }
                _runningCodes.Clear();
            }

            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"rr_connect?password={HttpUtility.UrlPathEncode(Options.Password)}&time={DateTime.Now:s}");
            using HttpResponseMessage response = await SendRequest(request, Options.Timeout, cancellationToken);
            response.EnsureSuccessStatusCode();

            using JsonDocument document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(), cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("err", out JsonElement errProperty) && errProperty.TryGetInt32(out int errValue))
            {
                switch (errValue)
                {
                    case 0: break;
                    case 1: throw new InvalidPasswordException();
                    case 2: throw new NoFreeSessionException();
                    default: throw new LoginException($"Received unknown err value {errValue}");
                }
            }
            if (document.RootElement.TryGetProperty("isEmulated", out JsonElement isEmulatedProperty) && isEmulatedProperty.GetBoolean())
            {
                // Don't attempt to use emulated endpoints since the remote server provides support for RESTful calls too
                throw new OperationCanceledException("HTTP backend is emulated");
            }
            if (!document.RootElement.TryGetProperty("apiLevel", out JsonElement apiLevelProperty) || !apiLevelProperty.TryGetInt32(out int apiLevel) || apiLevel < MinApiLevel)
            {
                throw new InvalidVersionException("Incompatible API level");
            }
        }

        /// <summary>
        /// Send a generic a HTTP request
        /// </summary>
        /// <param name="request">HTTP request to send</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>HTTP response</returns>
        protected override async ValueTask<HttpResponseMessage> SendRequest(HttpRequestMessage request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            HttpResponseMessage response = await base.SendRequest(request, timeout, cancellationToken);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable &&
                (request.Method != HttpMethod.Get || request.RequestUri.AbsolutePath != "/rr_reply"))
            {
                // RRF has run out of G-code replies, try to fetch rr_reply once more
                int replySeq;
                lock (_seqs)
                {
                    _seqs.TryGetValue("reply", out replySeq);
                }
                await GetGCodeReply(replySeq + 1);
            }
            return response;
        }

        /// <summary>
        /// Internal method to query to the object model
        /// </summary>
        /// <param name="key">Key to query</param>
        /// <param name="flags">Query flags</param>
        /// <returns>Received JSON</returns>
        private async Task<JsonDocument> GetObjectModel(string key, string flags)
        {
            string errorMessage = "Invalid number of maximum retries configured";
            for (int i = 0; i <= Options.MaxRetries; i++)
            {
                try
                {
                    string query = string.IsNullOrEmpty(key) ? $"rr_model?flags={flags}" : $"rr_model?key={key}&flags={flags}";
                    using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, query);
                    using HttpResponseMessage response = await SendRequest(request, Options.Timeout, _terminateSession.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        await using Stream stream = await response.Content.ReadAsStreamAsync();
                        return await JsonDocument.ParseAsync(stream, cancellationToken: _terminateSession.Token);
                    }

                    errorMessage = $"Server returned HTTP {response.StatusCode} {response.ReasonPhrase}";
                    if (response.StatusCode >= HttpStatusCode.InternalServerError)
                    {
                        break;
                    }
                }
                catch (OperationCanceledException oce)
                {
                    if (_terminateSession.IsCancellationRequested)
                    {
                        throw;
                    }
                    errorMessage = oce.Message;
                }
            }
            throw new HttpRequestException(errorMessage);
        }

        /// <summary>
        /// TCS to complete when the object model is up-to-date
        /// </summary>
        private readonly List<TaskCompletionSource<object>> _modelUpdateTcs = new List<TaskCompletionSource<object>>();

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
            lock (_modelUpdateTcs)
            {
                _modelUpdateTcs.Add(tcs);
            }

            CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_terminateSession.Token, cancellationToken);
            CancellationTokenRegistration ctsRegistration = cts.Token.Register(() => tcs.TrySetCanceled());
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
        /// Filename of the thumbnail collection
        /// </summary>
        private string _thumbnailFile = string.Empty;

        /// <summary>
        /// Maintain the HTTP session
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
                        if (Options.ObserveObjectModel)
                        {
                            // Request the limits if no sequence numbers have been set yet
                            bool isSeqsEmpty;
                            lock (_seqs)
                            {
                                isSeqsEmpty = (_seqs.Count == 0);
                            }

                            if (isSeqsEmpty)
                            {
                                using JsonDocument limitsDocument = await GetObjectModel("limits", "d99vn");
                                if (limitsDocument.RootElement.TryGetProperty("key", out JsonElement limitsKey) && limitsKey.GetString().Equals("limits", StringComparison.InvariantCultureIgnoreCase) &&
                                    limitsDocument.RootElement.TryGetProperty("result", out JsonElement limitsResult))
                                {
                                    lock (Model)
                                    {
                                        Model.UpdateFromFirmwareJson("limits", limitsResult);
                                    }
                                }
                            }

                            // Request the next status update
                            using JsonDocument statusDocument = await GetObjectModel(string.Empty, "d99fn");
                            if (statusDocument.RootElement.TryGetProperty("key", out JsonElement statusKey) && string.IsNullOrEmpty(statusKey.GetString()) &&
                                statusDocument.RootElement.TryGetProperty("result", out JsonElement statusResult))
                            {
                                // Update frequently changing properties
                                lock (Model)
                                {
                                    Model.UpdateFromFirmwareJson(string.Empty, statusResult);
                                    UpdateLayers();
                                }

                                // Update object model keys depending on the sequence numbers
                                foreach (JsonProperty seqProperty in statusResult.GetProperty("seqs").EnumerateObject())
                                {
                                    if (seqProperty.Value.ValueKind == JsonValueKind.Number)
                                    {
                                        int seq, newSeq = seqProperty.Value.GetInt32();
                                        lock (_seqs)
                                        {
                                            _seqs.TryGetValue(seqProperty.Name, out seq);
                                        }

                                        if (newSeq > seq)
                                        {
                                            if (seqProperty.Name == "reply")
                                            {
                                                await GetGCodeReply(newSeq);
                                            }
                                            else
                                            {
                                                int next = 0, offset = 0;
                                                do
                                                {
                                                    // Request the next model chunk
                                                    using JsonDocument keyDocument = await GetObjectModel(seqProperty.Name, (next == 0) ? "d99vn" : $"d99vna{next}");
                                                    offset = next;
                                                    next = keyDocument.RootElement.TryGetProperty("next", out JsonElement nextValue) ? nextValue.GetInt32() : 0;

                                                    if (keyDocument.RootElement.TryGetProperty("key", out JsonElement keyName) &&
                                                        keyDocument.RootElement.TryGetProperty("result", out JsonElement keyResult))
                                                    {
                                                        lock (_seqs)
                                                        {
                                                            _seqs[seqProperty.Name] = newSeq;
                                                        }

                                                        GCodeFileInfo fileInfoToUpdate = null;
                                                        lock (Model)
                                                        {
                                                            if (Model.UpdateFromFirmwareJson(keyName.GetString(), keyResult, offset, next == 0))
                                                            {
                                                                if (keyName.GetString() == "job" &&
                                                                    Model.Job.File.Thumbnails.Count != 0 && _thumbnailFile != Model.Job.File.FileName)
                                                                {
                                                                    _thumbnailFile = Model.Job.File.FileName;
                                                                    fileInfoToUpdate = (GCodeFileInfo)Model.Job.File.Clone();
                                                                }
                                                            }
                                                            else
                                                            {
                                                                // Invalid key
                                                                break;
                                                            }
                                                        }

                                                        if (fileInfoToUpdate != null)
                                                        {
                                                            await GetThumbnails(fileInfoToUpdate, _terminateSession.Token);
                                                            lock (Model)
                                                            {
                                                                Model.Job.File.Thumbnails.Assign(fileInfoToUpdate.Thumbnails);
                                                            }
                                                        }
                                                    }

                                                    // Check the index of the next element
                                                    offset = next;
                                                }
                                                while (next != 0);
                                            }
                                        }
                                    }
                                }

                                // Object model is now up-to-date
                                lock (_modelUpdateTcs)
                                {
                                    foreach (TaskCompletionSource<object> tcs in _modelUpdateTcs)
                                    {
                                        tcs.TrySetResult(null);
                                    }
                                    _modelUpdateTcs.Clear();
                                }
                            }
                            else
                            {
                                // Request only seqs.reply to know when to query rr_reply
                                using JsonDocument replySeqDocument = await GetObjectModel("seqs.reply", string.Empty);
                                if (replySeqDocument.RootElement.TryGetProperty("result", out JsonElement replySeqElement) &&
                                    replySeqElement.ValueKind == JsonValueKind.Number)
                                {
                                    int seq, newSeq = replySeqElement.GetInt32();
                                    lock (_seqs)
                                    {
                                        _seqs.TryGetValue("reply", out seq);
                                    }

                                    if (newSeq > seq)
                                    {
                                        await GetGCodeReply(newSeq);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception e) when (e is OperationCanceledException || e is HttpRequestException)
                    {
                        // This happens when the remote end is offline or unavailable
                        lock (Model)
                        {
                            Model.State.Status = MachineStatus.Disconnected;
                            Model.Global.Clear();
                        }
                    }

                    // Wait a moment before attempting to reconnect
                    try
                    {
                        await Task.Delay(2000, _terminateSession.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // can only occur if the session is disposed
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
        /// Number of the last layer
        /// </summary>
        private int _lastLayer = -1;

        /// <summary>
        /// Last recorded print duration
        /// </summary>
        private int _lastDuration;

        /// <summary>
        /// Filament usage at the time of the last layer change
        /// </summary>
        private List<float> _lastFilamentUsage = new List<float>();

        /// <summary>
        /// Last file position at the time of the last layer change
        /// </summary>
        private long _lastFilePosition;

        /// <summary>
        /// Last known Z height
        /// </summary>
        private float _lastHeight;

        /// <summary>
        /// Update the layers
        /// </summary>
        private void UpdateLayers()
        {
            // Are we printing?
            if (Model.Job.Duration == null)
            {
                if (_lastLayer != -1)
                {
                    _lastLayer = -1;
                    _lastDuration = 0;
                    _lastFilamentUsage.Clear();
                    _lastFilePosition = 0L;
                    _lastHeight = 0F;
                }
                return;
            }

            // Reset the layers when a new print is started
            if (_lastLayer == -1)
            {
                _lastLayer = 0;
                Model.Job.Layers.Clear();
            }

            // Don't continue from here unless the layer number is known
            if (Model.Job.Layer == null)
            {
                return;
            }

            int numChangedLayers = Math.Abs(Model.Job.Layer.Value - _lastLayer);
            if (numChangedLayers > 0 && Model.Job.Layer.Value > 0 && _lastLayer > 0)
            {
                // Compute average stats per changed layer
                int printDuration = Model.Job.Duration.Value - (Model.Job.WarmUpDuration != null ? Model.Job.WarmUpDuration.Value : 0);
                float avgLayerDuration = (printDuration - _lastDuration) / numChangedLayers;
                List<float> totalFilamentUsage = new List<float>(), avgFilamentUsage = new List<float>();
                long bytesPrinted = (Model.Job.FilePosition != null) ? (Model.Job.FilePosition.Value - _lastFilePosition) : 0L;
                float avgFractionPrinted = (Model.Job.File.Size > 0) ? (float)bytesPrinted / (Model.Job.File.Size * numChangedLayers) : 0F;
                for (int i = 0; i < Model.Move.Extruders.Count; i++)
                {
                    if (Model.Move.Extruders[i] != null)
                    {
                        float lastFilamentUsage = (i < _lastFilamentUsage.Count) ? _lastFilamentUsage[i] : 0F;
                        totalFilamentUsage.Add(Model.Move.Extruders[i].RawPosition);
                        avgFilamentUsage.Add((Model.Move.Extruders[i].RawPosition - lastFilamentUsage) / numChangedLayers);
                    }
                }
                float currentHeight = 0F;
                foreach (Axis axis in Model.Move.Axes)
                {
                    if (axis != null && axis.Letter == 'Z' && axis.UserPosition != null)
                    {
                        currentHeight = axis.UserPosition.Value;
                        break;
                    }
                }
                float avgHeight = Math.Abs(currentHeight - _lastHeight) / numChangedLayers;

                // Add missing layers
                for (int i = Model.Job.Layers.Count; i < Model.Job.Layer.Value - 1; i++)
                {
                    Layer newLayer = new Layer();
                    foreach (AnalogSensor sensor in Model.Sensors.Analog)
                    {
                        if (sensor != null)
                        {
                            newLayer.Temperatures.Add(sensor.LastReading);
                        }
                    }
                    newLayer.Height = avgHeight;
                    Model.Job.Layers.Add(newLayer);
                }

                // Merge data
                for (int i = Math.Min(_lastLayer, Model.Job.Layer.Value); i < Math.Max(_lastLayer, Model.Job.Layer.Value); i++)
                {
                    Layer layer = Model.Job.Layers[i - 1];
                    layer.Duration += avgLayerDuration;
                    for (int k = 0; k < avgFilamentUsage.Count; k++)
                    {
                        if (k >= layer.Filament.Count)
                        {
                            layer.Filament.Add(avgFilamentUsage[k]);
                        }
                        else
                        {
                            layer.Filament[k] += avgFilamentUsage[k];
                        }
                    }
                    layer.FractionPrinted += avgFractionPrinted;
                }

                // Record values for the next layer change
                _lastDuration = printDuration;
                _lastFilamentUsage = totalFilamentUsage;
                _lastFilePosition = Model.Job.FilePosition ?? 0L;
                _lastHeight = currentHeight;
            }
            _lastLayer = Model.Job.Layer.Value;
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

            // Cancel all running codes
            lock (_runningCodes)
            {
                foreach (TaskCompletionSource<string> tcs in _runningCodes.Keys)
                {
                    tcs.SetCanceled();
                }
                _runningCodes.Clear();
            }

            // Disconnect if possible
            try
            {
                using CancellationTokenSource cts = new CancellationTokenSource(Options.Timeout);
                await HttpClient.GetAsync("rr_disconnect", cts.Token);
            }
            catch
            {
                // ignored
            }

            // Dispose of the HTTP client
            HttpClient.Dispose();
        }

        /// <summary>
        /// Response to a G-code request
        /// </summary>
        private class GcodeReply
        {
            public int Buff { get; set; }
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
            // Check if we can expect a code reply at all
            bool canAwaitCode = false;
            using (StringReader codeStream = new StringReader(code))
            {
                Code codeObj = new Code();
                while (Code.Parse(codeStream, codeObj))
                {
                    if (codeObj.Type != CodeType.Comment &&
                        (codeObj.Type != CodeType.MCode || (codeObj.MajorNumber != 997 && codeObj.MajorNumber != 999)))
                    {
                        canAwaitCode = !executeAsynchronously;
                        break;
                    }
                    codeObj.Reset();
                }
            }

            // Get the current reply sequence number
            int replySeq;
            lock (_seqs)
            {
                if (!_seqs.TryGetValue("reply", out replySeq))
                {
                    replySeq = -1;
                }
            }

            // Make sure we know when to resolve the requested code
            if (replySeq == -1 && canAwaitCode)
            {
                using JsonDocument replySeqDocument = await GetObjectModel("seqs.reply", string.Empty);
                if (replySeqDocument.RootElement.TryGetProperty("result", out JsonElement replySeqElement) &&
                    replySeqElement.ValueKind == JsonValueKind.Number)
                {
                    replySeq = replySeqElement.GetInt32();
                    await GetGCodeReply(replySeq);
                }
            }

            // Send it to RRF
            Task<string> codeTask = null;
            string errorMessage = "Invalid number of maximum retries configured";
            for (int i = 0; i <= Options.MaxRetries; i++)
            {
                try
                {
                    using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"rr_gcode?gcode={HttpUtility.UrlPathEncode(code)}");
                    using HttpResponseMessage response = await SendRequest(request, Options.Timeout, cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        // Make sure the full G-code could be stored
                        await using Stream responseStream = await response.Content.ReadAsStreamAsync();
                        GcodeReply responseObj = await JsonSerializer.DeserializeAsync<GcodeReply>(responseStream, JsonHelper.DefaultJsonOptions, cancellationToken);
                        if (responseObj.Buff == 0)
                        {
                            throw new ArgumentException("G-code buffer is full");
                        }

                        // Stop here if no reply can be expected
                        if (!canAwaitCode)
                        {
                            return string.Empty;
                        }

                        // Enqueue this code request
                        TaskCompletionSource<string> codeRequest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                        lock (_runningCodes)
                        {
                            _runningCodes.Add(codeRequest, replySeq);
                        }
                        codeTask = codeRequest.Task;
                        break;
                    }

                    errorMessage = $"Server returned HTTP {response.StatusCode} {response.ReasonPhrase}";
                    if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                    {
                        await Task.Delay(2000, cancellationToken);
                    }
                    else if (response.StatusCode >= HttpStatusCode.InternalServerError)
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

            // Code has been started or it could not be transmitted
            if (codeTask != null)
            {
                return await codeTask;
            }
            throw new HttpRequestException(errorMessage);
        }

        /// <summary>
        /// Get the G-code reply
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private async Task GetGCodeReply(int seq)
        {
            string errorMessage = "Invalid number of maximum retries configured";
            for (int i = 0; i <= Options.MaxRetries; i++)
            {
                try
                {
                    using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "rr_reply");
                    using HttpResponseMessage response = await SendRequest(request, Options.Timeout, _terminateSession.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        // Get the reply and update the sequence number
                        string gcodeReply = await response.Content.ReadAsStringAsync();
                        lock (_seqs)
                        {
                            _seqs["reply"] = seq;
                        }

                        // Check if a code is waiting to be resolved
                        bool codeHandled = false;
                        lock (_runningCodes)
                        {
                            foreach (var kv in _runningCodes)
                            {
                                if (seq > kv.Value)
                                {
                                    kv.Key.SetResult(gcodeReply);
                                    codeHandled = true;
                                }
                            }

                            foreach (TaskCompletionSource<string> codeTask in _runningCodes.Keys.ToList())
                            {
                                if (codeTask.Task.IsCompleted)
                                {
                                    _runningCodes.Remove(codeTask);
                                }
                            }
                        }

                        // If not, check if the message can be stored
                        if (!codeHandled && Options.ObserveMessages)
                        {
                            lock (Model)
                            {
                                if (gcodeReply.StartsWith("Error: "))
                                {
                                    Model.Messages.Add(new Message(MessageType.Error, gcodeReply["Error: ".Length..]));
                                }
                                else if (gcodeReply.StartsWith("Warning: "))
                                {
                                    Model.Messages.Add(new Message(MessageType.Warning, gcodeReply["Warning: ".Length..]));
                                }
                                else
                                {
                                    Model.Messages.Add(new Message(MessageType.Success, gcodeReply));
                                }
                            }
                        }
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
                    if (_terminateSession.IsCancellationRequested)
                    {
                        throw;
                    }
                    errorMessage = oce.Message;
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
            // Compute the CRC32 checksum
            Crc32 crc32 = new Crc32();
            string checksum = string.Empty;
            foreach (byte b in crc32.ComputeHash(content))
            {
                checksum += b.ToString("x2").ToLower();
            }
            content.Seek(0, SeekOrigin.Begin);

            // Try to upload it
            string query = (lastModified != null) ? $"rr_upload?name={HttpUtility.UrlPathEncode(filename)}&time={lastModified:s}&crc32={checksum}" : $"rr_upload?name={HttpUtility.UrlPathEncode(filename)}&crc32={checksum}";
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, query);
            request.Content = new StreamContent(content);

            // Check if that worked
            using HttpResponseMessage response = await SendRequest(request, Timeout.InfiniteTimeSpan, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using Stream responseStream = await response.Content.ReadAsStreamAsync();
            ErrResponse responseObj = await JsonSerializer.DeserializeAsync<ErrResponse>(responseStream, JsonHelper.DefaultJsonOptions, cancellationToken);
            if (responseObj.Err != 0)
            {
                throw new HttpRequestException($"rr_upload returned err {responseObj.Err}");
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
                    using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"rr_delete?name={HttpUtility.UrlPathEncode(filename)}");
                    using HttpResponseMessage response = await SendRequest(request, Options.Timeout, cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        await using Stream responseStream = await response.Content.ReadAsStreamAsync();
                        ErrResponse responseObj = await JsonSerializer.DeserializeAsync<ErrResponse>(responseStream, JsonHelper.DefaultJsonOptions, cancellationToken);
                        if (responseObj.Err == 0)
                        {
                            return;
                        }
                        else if (responseObj.Err == 1)
                        {
                            throw new FileNotFoundException();
                        }
                        else
                        {
                            throw new HttpRequestException($"rr_delete returned err {responseObj.Err}");
                        }
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
                    using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"rr_move?old={HttpUtility.UrlPathEncode(from)}&new={HttpUtility.UrlPathEncode(to)}&deleteexisting={(force ? "yes" : "no")}");
                    using HttpResponseMessage response = await SendRequest(request, Options.Timeout, cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        await using Stream responseStream = await response.Content.ReadAsStreamAsync();
                        ErrResponse responseObj = await JsonSerializer.DeserializeAsync<ErrResponse>(responseStream, JsonHelper.DefaultJsonOptions, cancellationToken);
                        if (responseObj.Err == 0)
                        {
                            return;
                        }
                        else if (responseObj.Err == 1)
                        {
                            throw new FileNotFoundException();
                        }
                        else
                        {
                            throw new HttpRequestException($"rr_move returned err {responseObj.Err}");
                        }
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
                    using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"rr_mkdir?dir={HttpUtility.UrlPathEncode(directory)}");
                    using HttpResponseMessage response = await SendRequest(request, Options.Timeout, cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        await using Stream responseStream = await response.Content.ReadAsStreamAsync();
                        ErrResponse responseObj = await JsonSerializer.DeserializeAsync<ErrResponse>(responseStream, JsonHelper.DefaultJsonOptions, cancellationToken);
                        if (responseObj.Err == 0)
                        {
                            return;
                        }
                        else
                        {
                            throw new HttpRequestException($"rr_mkdir returned err {responseObj.Err}");
                        }
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
        public override async Task<HttpResponseMessage> Download(string filename, CancellationToken cancellationToken = default)
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"rr_download?name={HttpUtility.UrlPathEncode(filename)}");

            HttpResponseMessage response = await SendRequest(request, Timeout.InfiniteTimeSpan, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new FileNotFoundException();
            }

            response.EnsureSuccessStatusCode();
            return response;
        }

        /// <summary>
        /// Internal class for filelist items
        /// </summary>
        private class FileItem
        {
            public char Type { get; set; }
            public string Name { get; set; }
            public long Size { get; set; }
            public DateTime Date { get; set; }
        }

        /// <summary>
        /// Internal class for filelists
        /// </summary>
        private class FileListResponse : ErrResponse
        {
            //public string dir { get; set; }
            public int First { get; set; }
            public List<FileItem> Files { get; set; }
            public int Next { get; set; }
        }

        /// <summary>
        /// Enumerate all files and directories in the given directory
        /// </summary>
        /// <param name="directory">Directory to query</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>List of all files and directories</returns>
        public override async Task<IList<FileListItem>> GetFileList(string directory, CancellationToken cancellationToken = default)
        {
            List<FileListItem> result = new List<FileListItem>();

            int nextIndex = 0;
            do
            {
                string errorMessage = "Invalid number of maximum retries configured";
                for (int i = 0; i <= Options.MaxRetries; i++)
                {
                    try
                    {
                        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"rr_filelist?dir={HttpUtility.UrlPathEncode(directory)}&first={nextIndex}");
                        using HttpResponseMessage response = await SendRequest(request, Options.Timeout, cancellationToken);

                        if (response.IsSuccessStatusCode)
                        {
                            await using Stream responseStream = await response.Content.ReadAsStreamAsync();
                            FileListResponse responseObj = await JsonSerializer.DeserializeAsync<FileListResponse>(responseStream, JsonHelper.DefaultJsonOptions, cancellationToken);
                            if (responseObj.Err == 0)
                            {
                                foreach (FileItem item in responseObj.Files)
                                {
                                    result.Add(new FileListItem()
                                    {
                                        Filename = item.Name,
                                        IsDirectory = item.Type == 'd',
                                        LastModified = item.Date,
                                        Size = item.Size
                                    });
                                }
                                nextIndex = responseObj.Next;
                            }
                            else if (responseObj.Err == 2)
                            {
                                throw new DirectoryNotFoundException();
                            }
                            else
                            {
                                throw new HttpRequestException($"rr_filelist returned err {responseObj.Err}");
                            }
                            break;
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

                    if (i == Options.MaxRetries)
                    {
                        throw new HttpRequestException(errorMessage);
                    }
                }
            }
            while (nextIndex > 0);

            return result;
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
            string encodedFilename = HttpUtility.UrlPathEncode(filename);
            for (int i = 0; i <= Options.MaxRetries; i++)
            {
                try
                {
                    using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"rr_fileinfo?name={encodedFilename}");
                    using HttpResponseMessage response = await SendRequest(request, Options.Timeout, cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        await using Stream responseStream = await response.Content.ReadAsStreamAsync();
                        using JsonDocument responseJson = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
                        if (responseJson.RootElement.TryGetProperty("err", out JsonElement errValue) && errValue.ValueKind == JsonValueKind.Number)
                        {
                            int err = errValue.GetInt32();
                            if (err == 0)
                            {
                                GCodeFileInfo result = new GCodeFileInfo();
                                result.UpdateFromJson(responseJson.RootElement, false);

                                if (readThumbnailContent)
                                {
                                    await GetThumbnails(result, cancellationToken);
                                }

                                return result;
                            }
                            else if (err == 1)
                            {
                                throw new FileNotFoundException();
                            }
                            else
                            {
                                throw new HttpRequestException($"rr_mkdir returned err {err}");
                            }
                        }
                        else
                        {
                            throw new HttpRequestException("rr_fileinfo did not return an err value");
                        }
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

        private async Task GetThumbnails(GCodeFileInfo fileinfo, CancellationToken cancellationToken)
        {
            foreach (ThumbnailInfo thumbnail in fileinfo.Thumbnails)
            {
                if (thumbnail.Offset > 0)
                {
                    for (int k = 0; thumbnail.Data == null && k <= Options.MaxRetries; k++)
                    {
                        try
                        {
                            long offset = thumbnail.Offset;
                            StringBuilder thumbnailData = new StringBuilder();

                            do
                            {
                                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"rr_thumbnail?name={fileinfo.FileName}&offset={offset}");
                                using HttpResponseMessage response = await SendRequest(request, Options.Timeout, cancellationToken);

                                if (response.IsSuccessStatusCode)
                                {
                                    await using Stream responseStream = await response.Content.ReadAsStreamAsync();
                                    using JsonDocument responseJson = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
                                    if (responseJson.RootElement.TryGetProperty("err", out JsonElement errValue) && errValue.ValueKind == JsonValueKind.Number)
                                    {
                                        int err = errValue.GetInt32();
                                        if (err != 0)
                                        {
                                            throw new ArgumentException($"err {err}");
                                        }

                                        offset = responseJson.RootElement.GetProperty("next").GetInt32();
                                        thumbnailData.Append(responseJson.RootElement.GetProperty("data").GetString());
                                    }
                                }
                            } while (offset != 0);
                            thumbnail.Data = thumbnailData.ToString();
                        }
                        catch (Exception e)
                        {
                            thumbnail.Data = null;
                            if (!(e is HttpRequestException))
                            {
                                // Retries only apply to HTTP exceptions
                                break;
                            }
                        }
                    }
                }
            }
        }

        // ** Plugin and system package calls are not supported (yet) **
    }
}
