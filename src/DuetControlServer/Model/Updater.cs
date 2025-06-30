using DuetAPI.ObjectModel;
using DuetControlServer.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Model;

/// <summary>
/// Static helper class to merge the RepRapFirmware object model with ours
/// </summary>
/// <param name="firmwareUpdater">Firmware updater</param>
/// <param name="linkInterface">Link interface</param>
/// <param name="model">Object model to update</param>
/// <param name="settings">Settings</param>
public class Updater(FirmwareUpdater firmwareUpdater, Link.Interface linkInterface, ObjectModel model, IOptions<Settings> settings) : BackgroundService
{
    /// <summary>
    /// Logger instance
    /// </summary>
    private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// General-purpose lock for this class
    /// </summary>
    private static readonly AsyncLock _lock = new();

    /// <summary>
    /// First condition variable for object model updates
    /// </summary>
    private static readonly AsyncConditionVariable _updateConditionA = new(_lock);

    /// <summary>
    /// First condition variable for object model updates
    /// </summary>
    private static readonly AsyncConditionVariable _updateConditionB = new(_lock);

    /// <summary>
    /// Whether a client waiting for an object model update shall use A or B
    /// </summary>
    private static bool _waitForConditionA;

    /// <summary>
    /// Dictionary of main keys vs last sequence numbers
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _lastSeqs = new();

    /// <summary>
    /// Wait for the model to be fully updated from RepRapFirmware
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public static async Task WaitForFullUpdateAsync(CancellationToken cancellationToken = default)
    {
        using (await _lock.LockAsync(cancellationToken))
        {
            await (_waitForConditionA ? _updateConditionA : _updateConditionB).WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// Called in non-SPI mode to notify waiting tasks about a finished model update
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public static async Task MachineModelFullyUpdated(CancellationToken cancellationToken = default)
    {
        using (await _lock.LockAsync(cancellationToken))
        {
            _waitForConditionA = !_waitForConditionA;
            (_waitForConditionA ? _updateConditionA : _updateConditionB).NotifyAll();
        }
    }

    /// <summary>
    /// Process a config response (no longer supported or encouraged; for backwards-compatibility)
    /// </summary>
    /// <param name="response">Legacy config response</param>
    public void ProcessLegacyConfigResponse(byte[] response, CancellationToken cancellationToken = default)
    {
        using JsonDocument jsonDocument = JsonDocument.Parse(response);
        using (_lock.Lock(cancellationToken))
        {
            if (jsonDocument.RootElement.TryGetProperty("boardName", out JsonElement boardName))
            {
                using (model.AccessReadWrite(cancellationToken))
                {
                    model.Boards.Clear();
                    model.Boards.Add(new Board
                    {
                        IapFileNameSBC = $"Duet3_SBCiap_{boardName.GetString()}.bin",
                        FirmwareFileName = $"Duet3Firmware_{boardName.GetString()}.bin"
                    });
                }
                _logger.Warn("Deprecated firmware detected, please update it in order to use DSF");
            }
            else
            {
                // boardName field is not present - this must be a really old firmware version
                using (model.AccessReadWrite(cancellationToken))
                {
                    model.Boards.Clear();
                    model.Boards.Add(new Board
                    {
                        IapFileNameSBC = "Duet3_SBCiap_MB6HC.bin",
                        FirmwareFileName = "Duet3Firmware_MB6HC.bin"
                    });
                }
                _logger.Warn("Deprecated firmware detected, assuming legacy firmware files for MB6HC. You may have to use bossa to update it");
            }

            // Cannot perform any further updates...
            _waitForConditionA = !_waitForConditionA;
            (_waitForConditionA ? _updateConditionA : _updateConditionB).NotifyAll();

            // Check if the firmware is supposed to be updated
            if (settings.Value.UpdateOnly && !_updatingFirmware)
            {
                _updatingFirmware = true;
                _ = Task.Run(async () => await firmwareUpdater.UpdateFirmwareAsync(cancellationToken), cancellationToken);
            }
        }
    }

    private byte[] _jsonData = [];

    private string _requestedKey = string.Empty;

    private bool _keyUpdated = false;

    private readonly List<string> _updatedKeys = [];

    private async Task RequestModel(string key, string flags)
    {
        _requestedKey = key;
        _jsonData = await linkInterface.RequestObjectModel(key, flags);
    }

    private int UpdateModel(int? offset = null)
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

                                    if (axisCount >= (model.Limits.ReportedAxes ?? 9))
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
                                    if (!_lastSeqs.TryGetValue(seqKey, out int lastSeq) || lastSeq != seq)
                                    {
                                        _updatedKeys.Add(seqKey);
                                        _lastSeqs[seqKey] = seq;
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
                    _keyUpdated = model.UpdateFromFirmwareJsonReader(_requestedKey, ref reader, offset ?? 0, last);
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
        if (settings.Value.NoSpi)
        {
            // Don't start if no SPI connection is available
            await Task.Delay(-1, stoppingToken);
        }

        do
        {
            try
            {
                // Starting the next OM update. Waiting clients can be notified after this one,
                // but clients requesting an update while the OM is being updated should wait for the next one to complete first
                _waitForConditionA = !_waitForConditionA;

                // Request the limits if no sequence numbers have been set yet
                using (await _lock.LockAsync(stoppingToken))
                {
                    if (_lastSeqs.IsEmpty)
                    {
                        await RequestModel("limits", "d99vno");
                        using (await model.AccessReadWriteAsync(stoppingToken))
                        {
                            UpdateModel();
                            if (_keyUpdated)
                            {
                                _logger.Debug("Updated key limits");
                            }
                        }
                    }
                }

                // Request the next status update
                await RequestModel(string.Empty, "d99fno");

                // Update frequently changing properties
                using (await model.AccessReadWriteAsync(stoppingToken))
                {
                    UpdateModel();
                    if (model.IsUpdating && model.State.Status != MachineStatus.Updating)
                    {
                        model.State.Status = MachineStatus.Updating;
                    }
                    UpdateLayers();
                }

                // Update changed object model keys
                for (int i = 0; i < _updatedKeys.Count; i++)
                {
                    string key = _updatedKeys[i];
                    if (key != "reply" && (!settings.Value.UpdateOnly || key is "boards" or "directories" or "state"))
                    {
                        _logger.Debug(() => $"Requesting update of key {key}, new seq {_lastSeqs[key]}");

                        int next = 0;
                        do
                        {
                            await RequestModel(key, (next == 0) ? "d99vno" : $"d99vnoa{next}");

                            int offset = next;
                            using (await model.AccessReadWriteAsync(stoppingToken))
                            {
                                next = UpdateModel(offset);
                                if (_keyUpdated)
                                {
                                    _logger.Debug("Updated key {0}{1}", key, (offset + next != 0) ? $" starting from {offset}, next {next}" : string.Empty);
                                }
                                else
                                {
                                    _logger.Warn($"Invalid key {key} in the object model");
                                    break;
                                }

                                if (model.IsUpdating && model.State.Status != MachineStatus.Updating)
                                {
                                    model.State.Status = MachineStatus.Updating;
                                }
                            }
                        }
                        while (next != 0);
                    }
                }

                // Object model is now up-to-date, notify waiting clients
                (_waitForConditionA ? _updateConditionB : _updateConditionA).NotifyAll();

                // Check if the firmware is supposed to be updated
                if (settings.Value.UpdateOnly && !_updatingFirmware)
                {
                    _updatingFirmware = true;
                    _ = Task.Run(async () => await firmwareUpdater.UpdateFirmwareAsync(stoppingToken), stoppingToken);
                }
            }
            catch (InvalidOperationException e)
            {
                _logger.Error(e, "Failed to merge JSON due to internal error: {0}", Encoding.UTF8.GetString(_jsonData));
            }
            catch (OperationCanceledException)
            {
                // RRF has disconnected, try again later
            }

            // Wait a moment
            await Task.Delay(settings.Value.ModelUpdateInterval, stoppingToken);
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
    private List<float> _lastFilamentUsage = [];

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
        if (model.Job.Duration is null)
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
            model.Job.Layers.Clear();
        }

        // Don't continue from here unless the layer number is known and valid
        if (model.Job.Layer is null || model.Job.Layer.Value < 0)
        {
            return;
        }

        if (model.Job.Layer.Value > 0 && model.Job.Layer.Value != _lastLayer)
        {
            // Compute layer usage stats first
            int numChangedLayers = (model.Job.Layer.Value > _lastLayer) ? Math.Abs(model.Job.Layer.Value - _lastLayer) : 1;
            int printDuration = model.Job.Duration.Value - (model.Job.WarmUpDuration is not null ? model.Job.WarmUpDuration.Value : 0);
            float avgLayerDuration = (printDuration - _lastDuration) / numChangedLayers;
            List<float> totalFilamentUsage = [], avgFilamentUsage = [];
            long bytesPrinted = (model.Job.FilePosition is not null) ? (model.Job.FilePosition.Value - _lastFilePosition) : 0L;
            float avgFractionPrinted = (model.Job.File.Size > 0) ? (float)bytesPrinted / (model.Job.File.Size * numChangedLayers) : 0F;
            for (int i = 0; i < model.Move.Extruders.Count; i++)
            {
                if (model.Move.Extruders[i] is not null)
                {
                    float lastFilamentUsage = (i < _lastFilamentUsage.Count) ? _lastFilamentUsage[i] : 0F;
                    totalFilamentUsage.Add(model.Move.Extruders[i].RawPosition);
                    avgFilamentUsage.Add((model.Move.Extruders[i].RawPosition - lastFilamentUsage) / numChangedLayers);
                }
            }

            // Get layer height
            float currentHeight = 0F;
            foreach (Axis axis in model.Move.Axes)
            {
                if (axis is { Letter: 'Z', UserPosition: {} })
                {
                    currentHeight = axis.UserPosition.Value;
                    break;
                }
            }
            float avgLayerHeight = Math.Abs(currentHeight - _lastHeight) / Math.Abs(model.Job.Layer.Value - _lastLayer);

            if (model.Job.Layer > _lastLayer)
            {
                // Add new layers
                for (int i = model.Job.Layers.Count; i < model.Job.Layer.Value - 1; i++)
                {
                    Layer newLayer = new()
                    {
                        Duration = avgLayerDuration
                    };
                    foreach (float filamentUsage in avgFilamentUsage)
                    {
                        newLayer.Filament.Add(filamentUsage);
                    }
                    newLayer.FractionPrinted = avgFractionPrinted;
                    newLayer.Height = avgLayerHeight;
                    foreach (AnalogSensor? sensor in model.Sensors.Analog)
                    {
                        if (sensor is not null)
                        {
                            newLayer.Temperatures.Add(sensor.LastReading);
                        }
                    }
                    model.Job.Layers.Add(newLayer);
                }
            }
            else if (model.Job.Layer < _lastLayer)
            {
                // Layer count went down (probably printing sequentially), update the last layer
                Layer lastLayer;
                if (model.Job.Layers.Count < _lastLayer)
                {
                    lastLayer = new()
                    {
                        Height = avgLayerHeight
                    };
                    foreach (AnalogSensor? sensor in model.Sensors.Analog)
                    {
                        if (sensor is not null)
                        {
                            lastLayer.Temperatures.Add(sensor.LastReading);
                        }
                    }
                    model.Job.Layers.Add(lastLayer);
                }
                else
                {
                    lastLayer = model.Job.Layers[_lastLayer - 1];
                }

                lastLayer.Duration += avgLayerDuration;
                for (int i = 0; i < avgFilamentUsage.Count; i++)
                {
                    if (i >= lastLayer.Filament.Count)
                    {
                        lastLayer.Filament.Add(avgFilamentUsage[i]);
                    }
                    else
                    {
                        lastLayer.Filament[i] += avgFilamentUsage[i];
                    }
                }
                lastLayer.FractionPrinted += avgFractionPrinted;
            }

            // Record values for the next layer change
            _lastDuration = printDuration;
            _lastFilamentUsage = totalFilamentUsage;
            _lastFilePosition = model.Job.FilePosition ?? 0L;
            _lastHeight = currentHeight;
            _lastLayer = model.Job.Layer.Value;
        }
    }

    /// <summary>
    /// Called by the SPI subsystem when the connection to the Duet has been lost
    /// </summary>
    public void ConnectionLost()
    {
        using (model.AccessReadWrite())
        {
            model.Boards.Clear();
            model.Global.Clear();
            if (model.State.Status != MachineStatus.Halted && model.State.Status != MachineStatus.Updating)
            {
                model.State.Status = MachineStatus.Disconnected;
            }
            model.State.DisplayMessage = string.Empty;
            model.State.MessageBox = null;
        }

        _lastSeqs.Clear();
    }
}
