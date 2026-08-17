using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Link;
using DuetControlServer.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Model;

/// <summary>
/// Service to keep the object model up-to-date with the firmware
/// </summary>
public class UpdateService : BackgroundService
{
    // Private fields
    private readonly FirmwareUpdater _firmwareUpdater;
    private readonly LinkInterface _linkInterface;
    private readonly ObjectModel _model;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<UpdateService> _logger;
    private readonly Settings _settings;

    /// <summary>
    /// Constructor of this class
    /// </summary>
    /// <param name="firmwareUpdater">Firmware updater</param>
    /// <param name="linkInterface">Link interface</param>
    /// <param name="model">Object model</param>
    /// <param name="lifetime">Application lifetime</param>
    /// <param name="logger">Logger</param>
    /// <param name="settings">Settings</param>
    public UpdateService(FirmwareUpdater firmwareUpdater, LinkInterface linkInterface, ObjectModel model, IHostApplicationLifetime lifetime, ILogger<UpdateService> logger, IOptions<Settings> settings)
    {
        _firmwareUpdater = firmwareUpdater;
        _linkInterface = linkInterface;
        _model = model;
        _lifetime = lifetime;
        _logger = logger;
        _settings = settings.Value;
    }

    /// <inheritdoc />
    public override Task StopAsync(CancellationToken cancellationToken) => base.StopAsync(cancellationToken);

    // Data for object model updates
    private byte[] _jsonData = [];
    private string _requestedKey = string.Empty;
    private bool _keyUpdated = false;
    private readonly List<string> _updatedKeys = [];

    /// <summary>
    /// Whether the last object model queries asked RRF for fields flagged as verbose
    /// </summary>
    private bool _verboseQueries;

    /// <summary>
    /// Whether the last object model queries asked RRF for fields flagged as obsolete
    /// </summary>
    private bool _obsoleteQueries;

    /// <summary>
    /// Request the object model from the firmware
    /// </summary>
    /// <param name="key">Key to query</param>
    /// <param name="flags">Query flags</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    private async Task RequestModelAsync(string key, string flags, CancellationToken cancellationToken = default)
    {
        _requestedKey = key;
        _jsonData = await _linkInterface.RequestObjectModel(key, flags, cancellationToken);
    }

    /// <summary>
    /// Update the object model from the JSON data received from the firmware
    /// </summary>
    /// <param name="scope">Extent of the received data</param>
    /// <param name="offset">Optional array start offset</param>
    /// <returns>Next array offset to query</returns>
    private int UpdateModel(ModelUpdateScope scope, int? offset = null)
    {
        bool last = true;
        Utf8JsonReader reader;

        // Determine the next value to query. That also lets us know if this is the last update.
        // This is only required for arrays
        int next = 0;
        if (offset != null || _requestedKey == "move")
        {
            reader = new(_jsonData);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (offset != null && reader.ValueTextEquals("next") && reader.Read())
                    {
                        // Get the next index to query
                        next = reader.GetInt32();
                        last = next == 0;
                    }
                    else if (_requestedKey == "move" && reader.ValueTextEquals("result"u8) && reader.Read())
                    {
                        // Check if move.axes needs an extra query
                        Utf8JsonReader readerCopy = reader;
                        while (readerCopy.Read() && readerCopy.TokenType != JsonTokenType.EndObject)
                        {
                            if (readerCopy.TokenType == JsonTokenType.PropertyName)
                            {
                                string propertyName = readerCopy.GetString()!;
                                if (propertyName == "axes")
                                {
                                    int axisCount = 0;
                                    while (readerCopy.Read() && readerCopy.TokenType != JsonTokenType.EndArray)
                                    {
                                        if (readerCopy.TokenType == JsonTokenType.StartObject)
                                        {
                                            axisCount++;
                                            readerCopy.Skip();
                                        }
                                    }

                                    if (axisCount >= (_model.Limits.ReportedAxes ?? 9))
                                    {
                                        _updatedKeys.Add("move.axes");
                                        last = false;   // Don't delete missing items from the axis array yet
                                    }
                                    break;
                                }
                            }
                        }
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
            }
        }

        // Update data
        reader = new(_jsonData);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("result"u8) && reader.Read())
                {
                    if (_requestedKey is "" or "seqs")
                    {
                        _updatedKeys.Clear();

                        // Update sequence numbers if applicable
                        Utf8JsonReader readerCopy = reader;
                        if (_requestedKey != "seqs")
                        {
                            // Jump to start of seqs key. This isn't necessary if "seqs" was explicitly requested
                            while (readerCopy.Read() && readerCopy.TokenType != JsonTokenType.EndObject)
                            {
                                if (readerCopy.TokenType == JsonTokenType.PropertyName)
                                {
                                    string propertyName = readerCopy.GetString()!;
                                    if (propertyName == "seqs")
                                    {
                                        readerCopy.Read();
                                        break;
                                    }
                                    else
                                    {
                                        readerCopy.Skip();
                                    }
                                }
                            }
                        }

                        // Process numeric sequence numbers
                        while (readerCopy.Read() && readerCopy.TokenType != JsonTokenType.EndObject)
                        {
                            if (readerCopy.TokenType == JsonTokenType.PropertyName)
                            {
                                string seqKey = readerCopy.GetString()!;
                                if (readerCopy.Read() && readerCopy.TokenType == JsonTokenType.Number)
                                {
                                    int seq = readerCopy.GetInt32();
                                    if (!_model.Seqs.TryGetValue(seqKey, out int lastSeq) || lastSeq != seq)
                                    {
                                        _updatedKeys.Add(seqKey);
                                        _model.Seqs[seqKey] = seq;
                                    }
                                }
                                else
                                {
                                    readerCopy.Skip();
                                }
                            }
                        }
                    }

                    // Update object model
                    _keyUpdated = _model.UpdateFromFirmwareJsonReader(_requestedKey, ref reader, offset ?? 0, last, scope);
                }
                else
                {
                    reader.Skip();
                }
            }
        }

        return next;
    }

    /// <summary>
    /// Process status updates in the background
    /// </summary>
    /// <returns>Asynchronous task</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        do
        {
            try
            {
#if false
                // Starting the next OM update. Waiting clients can be notified after this one,
                // but clients requesting an update while the OM is being updated should wait for the next one to complete first
                updateInterface.WaitForConditionA = !updateInterface.WaitForConditionA;
#endif

                // Verbose and obsolete fields are read once on start-up and from then on only while a
                // subscriber asks for them. Sequence numbers do not change just because a client changed its
                // mind, so drop them to have every key read again. Losing the last subscriber needs no
                // resync, the values that were already read simply go stale
                if ((IPC.Processors.ModelSubscription.VerboseSubscribers && !_verboseQueries) ||
                    (IPC.Processors.ModelSubscription.ObsoleteSubscribers && !_obsoleteQueries))
                {
                    _logger.LogDebug("Resyncing object model because a subscriber requires extra fields");
                    using (await _model.AccessReadWriteAsync(stoppingToken))
                    {
                        _model.Seqs.Clear();
                    }
                }
                _verboseQueries = IPC.Processors.ModelSubscription.VerboseSubscribers;
                _obsoleteQueries = IPC.Processors.ModelSubscription.ObsoleteSubscribers;

                // Nulls are no longer requested from RRF but reconstructed from the properties missing in the
                // response, so every scope must mirror the flags of the query that delivered the data
                bool initialQuery = _model.Seqs.Count == 0, queryVerbose = _verboseQueries || initialQuery, queryObsolete = _obsoleteQueries || initialQuery;
                string keyFlags = "d99" + (queryVerbose ? "v" : string.Empty) + (queryObsolete ? "o" : string.Empty);
                ModelUpdateScope keyScope = ModelUpdateScope.Full;
                if (queryVerbose)
                {
                    keyScope |= ModelUpdateScope.Verbose;
                }
                if (queryObsolete)
                {
                    keyScope |= ModelUpdateScope.Obsolete;
                }

                // Request the limits if no sequence numbers have been set yet
                if (_model.Seqs.Count == 0)
                {
                    _logger.LogDebug("Requesting initial limits");

                    await RequestModelAsync("limits", keyFlags, stoppingToken);
                    using (await _model.AccessReadWriteAsync(stoppingToken))
                    {
                        UpdateModel(keyScope);
                        if (_keyUpdated)
                        {
                            _logger.LogDebug("Updated key limits");
                        }
                    }
                }

                // Request the next status update. Obsolete fields are left out unless somebody wants them,
                // else every poll would carry the deprecated live values for nobody's benefit
                await RequestModelAsync(string.Empty, _obsoleteQueries ? "d99fo" : "d99f", stoppingToken);

                // Update frequently changing properties
                using (await _model.AccessReadWriteAsync(stoppingToken))
                {
                    UpdateModel(_obsoleteQueries ? (ModelUpdateScope.Live | ModelUpdateScope.Obsolete) : ModelUpdateScope.Live);
                    if (_model.IsUpdating && _model.State.Status != MachineStatus.Updating)
                    {
                        _model.State.Status = MachineStatus.Updating;
                    }
                    UpdateLayers();
                }

                // Update changed object model keys
                for (int i = 0; i < _updatedKeys.Count; i++)
                {
                    string key = _updatedKeys[i];
                    if (key != "reply" && (!_settings.UpdateOnly || key is "boards" or "directories" or "state"))
                    {
                        _logger.LogDebug("Requesting update of key {Key}, new seq {Seq}", key, _model.Seqs.TryGetValue(key, out int seqValue) ? seqValue : -1);

                        int next = 0;
                        do
                        {
                            await RequestModelAsync(key, (next == 0) ? keyFlags : $"{keyFlags}a{next}", stoppingToken);

                            int offset = next;
                            using (await _model.AccessReadWriteAsync(stoppingToken))
                            {
                                next = UpdateModel(keyScope, offset);
                                if (_keyUpdated)
                                {
                                    _logger.LogDebug("Updated key {Key}{Annotation}", key, (offset + next != 0) ? $" starting from {offset}, next {next}" : string.Empty);
                                }
                                else
                                {
                                    _logger.LogWarning("Invalid key {Key} in the object model", key);
                                    break;
                                }

                                if (_model.IsUpdating && _model.State.Status != MachineStatus.Updating)
                                {
                                    _model.State.Status = MachineStatus.Updating;
                                }
                            }
                        }
                        while (next != 0);
                    }
                }

                // Object model is now up-to-date, notify waiting clients
                await _model.FullyUpdatedAsync(stoppingToken);

                // Check if the firmware is supposed to be updated
                if (_settings.UpdateOnly && !_updatingFirmware)
                {
                    _updatingFirmware = true;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _firmwareUpdater.UpdateFirmwareAsync(stoppingToken);
                        }
                        catch (Exception e)
                        {
                            // In update-only mode the process must terminate even if the update fails,
                            // else dcs -u hangs forever without a diagnostic
                            if (e is not OperationCanceledException)
                            {
                                _logger.LogError(e, "Failed to update firmware");
                            }
                            _lifetime.StopApplication();
                        }
                    }, stoppingToken);
                }

                // Wait a moment
                await Task.Delay(_settings.ModelUpdateInterval, stoppingToken);
            }
            catch (ArgumentException ae)
            {
                _logger.LogWarning("Ignored object model response for key {Key}: {Message}", _requestedKey, ae.Message);
            }
            catch (InvalidOperationException e)
            {
                _logger.LogError(e, "Failed to merge JSON due to internal error: {JSON}", Encoding.UTF8.GetString(_jsonData));
            }
            catch (JsonException je)
            {
                _logger.LogError(je, "Failed to parse received JSON from key {0}: {1}", _requestedKey, Encoding.UTF8.GetString(_jsonData));
                throw;
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
        }
        while (!stoppingToken.IsCancellationRequested);
    }

    /// <summary>
    /// Indicates if the firmware is being updated
    /// </summary>
    private static bool _updatingFirmware;

    /// <summary>
    /// Number of the last layer
    /// </summary>
    private static int _lastLayer = -1;

    /// <summary>
    /// Last recorded print duration
    /// </summary>
    private static int _lastDuration;

    /// <summary>
    /// Filament usage at the time of the last layer change
    /// </summary>
    private static List<float> _lastFilamentUsage = [];

    /// <summary>
    /// Filament usage at the time of the last layer change
    /// </summary>
    private float _lastTotalFilamentUsage = 0F;

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
        if (_model.Job.Duration is null)
        {
            if (_lastLayer != -1)
            {
                _lastLayer = -1;
                _lastDuration = 0;
                _lastFilamentUsage.Clear();
                _lastTotalFilamentUsage = 0F;
                _lastFilePosition = 0L;
                _lastHeight = 0F;
            }
            return;
        }

        // Reset the layers when a new print is started
        if (_lastLayer == -1)
        {
            _lastLayer = 0;
            _model.Job.Layers.Clear();
        }

        // Don't continue from here unless the layer number is known and valid
        if (_model.Job.Layer is null || _model.Job.Layer.Value < 0)
        {
            return;
        }

        if (_model.Job.Layer.Value > 0 && _model.Job.Layer.Value != _lastLayer)
        {
            // Compute layer usage stats first
            int numChangedLayers = (_model.Job.Layer.Value > _lastLayer) ? Math.Abs(_model.Job.Layer.Value - _lastLayer) : 1;
            int printDuration = _model.Job.Duration.Value - (_model.Job.WarmUpDuration is not null ? _model.Job.WarmUpDuration.Value : 0);
            float avgLayerDuration = (float)(printDuration - _lastDuration) / numChangedLayers;
            long bytesPrinted = (_model.Job.FilePosition is not null) ? (_model.Job.FilePosition.Value - _lastFilePosition) : 0L;
            float avgFractionPrinted = (_model.Job.File.Size > 0) ? (float)bytesPrinted / (_model.Job.File.Size * numChangedLayers) : 0F;
            #region deprecated, to be removed in v3.8
            List<float> totalFilamentUsage = [], avgFilamentUsage = [];
            for (int i = 0; i < _model.Move.Extruders.Count; i++)
            {
                if (_model.Move.Extruders[i] is not null)
                {
                    float lastFilamentUsage = (i < _lastFilamentUsage.Count) ? _lastFilamentUsage[i] : 0F;
                    totalFilamentUsage.Add(_model.Move.Extruders[i].RawPosition);
                    avgFilamentUsage.Add((_model.Move.Extruders[i].RawPosition - lastFilamentUsage) / numChangedLayers);
                }
            }
            #endregion
            float totalAvgFilamentUsage = (_model.Job.RawExtrusion != null) ? (_model.Job.RawExtrusion.Value - _lastTotalFilamentUsage) / numChangedLayers : 0F;

            // Get layer height
            float currentHeight = 0F;
            foreach (Axis axis in _model.Move.Axes)
            {
                if (axis is { Letter: 'Z', UserPosition: {} })
                {
                    currentHeight = axis.UserPosition.Value;
                    break;
                }
            }
            float avgLayerHeight = Math.Abs(currentHeight - _lastHeight) / Math.Abs(_model.Job.Layer.Value - _lastLayer);

            if (_model.Job.Layer > _lastLayer)
            {
                // Add new layers
                for (int i = _model.Job.Layers.Count; i < _model.Job.Layer.Value - 1; i++)
                {
                    Layer newLayer = new()
                    {
                        Duration = avgLayerDuration,
                        FilamentUsage = totalAvgFilamentUsage
                    };
                    foreach (float filamentUsage in avgFilamentUsage)
                    {
#pragma warning disable CS0618 // Type or member is obsolete
                        newLayer.Filament.Add(filamentUsage);
#pragma warning restore CS0618 // Type or member is obsolete
                    }
                    newLayer.FractionPrinted = avgFractionPrinted;
                    newLayer.Height = avgLayerHeight;
                    foreach (AnalogSensor? sensor in _model.Sensors.Analog)
                    {
                        if (sensor is not null)
                        {
                            newLayer.Temperatures.Add(sensor.LastReading);
                        }
                    }
                    _model.Job.Layers.Add(newLayer);
                }
            }
            else if (_model.Job.Layer < _lastLayer)
            {
                // Layer count went down (probably printing sequentially), update the last layer
                Layer lastLayer;
                if (_model.Job.Layers.Count < _lastLayer)
                {
                    lastLayer = new()
                    {
                        Height = avgLayerHeight
                    };
                    foreach (AnalogSensor? sensor in _model.Sensors.Analog)
                    {
                        if (sensor is not null)
                        {
                            lastLayer.Temperatures.Add(sensor.LastReading);
                        }
                    }
                    _model.Job.Layers.Add(lastLayer);
                }
                else
                {
                    lastLayer = _model.Job.Layers[_lastLayer - 1];
                }

                lastLayer.Duration += avgLayerDuration;
                for (int i = 0; i < avgFilamentUsage.Count; i++)
                {
#pragma warning disable CS0618 // Type or member is obsolete
                    if (i >= lastLayer.Filament.Count)
                    {
                        lastLayer.Filament.Add(avgFilamentUsage[i]);
                    }
                    else
                    {
                        lastLayer.Filament[i] += avgFilamentUsage[i];
                    }
#pragma warning restore CS0618 // Type or member is obsolete
                }
                lastLayer.FractionPrinted += avgFractionPrinted;
            }

            // Record values for the next layer change
            _lastDuration = printDuration;
            _lastFilamentUsage = totalFilamentUsage;
            _lastTotalFilamentUsage = _model.Job.RawExtrusion ?? 0F;
            _lastFilePosition = _model.Job.FilePosition ?? 0L;
            _lastHeight = currentHeight;
            _lastLayer = _model.Job.Layer.Value;
        }
    }
}
