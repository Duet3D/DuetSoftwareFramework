using DuetAPI.ObjectModel;
using Nito.AsyncEx;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Model
{
    /// <summary>
    /// Static helper class to merge the RepRapFirmware object model with ours
    /// </summary>
    public static class Updater
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
        private static readonly ConcurrentDictionary<string, int> _lastSeqs = new();

        /// <summary>
        /// Wait for the model to be fully updated from RepRapFirmware
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public static async Task WaitForFullUpdate(CancellationToken cancellationToken)
        {
            using (await _lock.LockAsync(cancellationToken))
            {
                await (_waitForConditionA ? _updateConditionA : _updateConditionB).WaitAsync(cancellationToken);
                Program.CancellationToken.ThrowIfCancellationRequested();
            }
        }

        /// <summary>
        /// Wait for the model to be fully updated from RepRapFirmware
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static Task WaitForFullUpdate() => WaitForFullUpdate(Program.CancellationToken);

        /// <summary>
        /// Called in non-SPI mode to notify waiting tasks about a finished model update
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task MachineModelFullyUpdated()
        {
            using (await _lock.LockAsync(Program.CancellationToken))
            {
                _waitForConditionA = !_waitForConditionA;
                (_waitForConditionA ? _updateConditionA : _updateConditionB).NotifyAll();
            }
        }

        /// <summary>
        /// Process a config response (no longer supported or encouraged; for backwards-compatibility)
        /// </summary>
        /// <param name="response">Legacy config response</param>
        public static void ProcessLegacyConfigResponse(byte[] response)
        {
            using JsonDocument jsonDocument = JsonDocument.Parse(response);
            using (_lock.Lock(Program.CancellationToken))
            {
                if (jsonDocument.RootElement.TryGetProperty("boardName", out JsonElement boardName))
                {
                    using (Provider.AccessReadWrite())
                    {
                        Provider.Get.Boards.Clear();
                        Provider.Get.Boards.Add(new Board
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
                    using (Provider.AccessReadWrite())
                    {
                        Provider.Get.Boards.Clear();
                        Provider.Get.Boards.Add(new Board
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
                if (Settings.UpdateOnly && !_updatingFirmware)
                {
                    _updatingFirmware = true;
                    _ = Task.Run(Utility.Firmware.UpdateFirmware);
                }
            }
        }

        private static byte[] _jsonData = [];

        private static string _requestedKey = string.Empty;

        private static bool _keyUpdated = false;

        private static List<string> _updatedKeys = [];

        private static async Task RequestModel(string key, string flags)
        {
            _requestedKey = key;
            _jsonData = await SPI.Interface.RequestObjectModel(key, flags);
        }

        private static int UpdateModel(int offset = 0, bool last = true)
        {
            int next = 0;

            Utf8JsonReader reader = new(_jsonData);
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
                        else if (_requestedKey == "move")
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
                                            }
                                        }

                                        if (axisCount >= 9)
                                        {
                                            _updatedKeys.Add("move.axes");
                                        }
                                        break;
                                    }
                                }
                            }
                        }

                        // Update object model
                        _keyUpdated = Provider.Get.UpdateFromFirmwareJsonReader(_requestedKey, ref reader, offset, last);
                    }
                    else if (reader.ValueTextEquals("next") && reader.Read())
                    {
                        next = reader.GetInt32();
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
        public static async Task Run()
        {
            if (Settings.NoSpi)
            {
                // Don't start if no SPI connection is available
                await Task.Delay(-1, Program.CancellationToken);
            }

            do
            {
                try
                {
                    // Starting the next OM update. Waiting clients can be notified after this one,
                    // but clients requesting an update while the OM is being updated should wait for the next one to complete first
                    _waitForConditionA = !_waitForConditionA;

                    // Request the limits if no sequence numbers have been set yet
                    using (await _lock.LockAsync(Program.CancellationToken))
                    {
                        if (_lastSeqs.IsEmpty)
                        {
                            await RequestModel("limits", "d99vno");
                            using (await Provider.AccessReadWriteAsync())
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
                    using (await Provider.AccessReadWriteAsync())
                    {
                        UpdateModel();
                        if (Provider.IsUpdating && Provider.Get.State.Status != MachineStatus.Updating)
                        {
                            Provider.Get.State.Status = MachineStatus.Updating;
                        }
                        UpdateLayers();
                    }

                    // Update changed object model keys
                    foreach (string key in _updatedKeys)
                    {
                        if (key != "reply" && (!Settings.UpdateOnly || key is "boards" or "directories" or "state"))
                        {
                            _logger.Debug(() => $"Requesting update of key {key}, new seq {_lastSeqs[key]}");

                            int next = 0;
                            do
                            {
                                await RequestModel(key, (next == 0) ? "d99vno" : $"d99vnoa{next}");

                                int offset = next;
                                using (await Provider.AccessReadWriteAsync())
                                {
                                    next = UpdateModel(offset, next == 0);
                                    if (_keyUpdated)
                                    {
                                        _logger.Debug("Updated key {0}{1}", key, (offset + next != 0) ? $" starting from {offset}, next {next}" : string.Empty);
                                    }
                                    else
                                    {
                                        _logger.Warn($"Invalid key {key} in the object model");
                                        break;
                                    }

                                    if (Provider.IsUpdating && Provider.Get.State.Status != MachineStatus.Updating)
                                    {
                                        Provider.Get.State.Status = MachineStatus.Updating;
                                    }
                                }
                            } while (next != 0);
                        }
                    }

                    // Object model is now up-to-date, notify waiting clients
                    (_waitForConditionA ? _updateConditionB : _updateConditionA).NotifyAll();

                    // Check if the firmware is supposed to be updated
                    if (Settings.UpdateOnly && !_updatingFirmware)
                    {
                        _updatingFirmware = true;
                        _ = Task.Run(Utility.Firmware.UpdateFirmware);
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
                await Task.Delay(Settings.ModelUpdateInterval, Program.CancellationToken);
            }
            while (!Program.CancellationToken.IsCancellationRequested);
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
        /// Last file position at the time of the last layer change
        /// </summary>
        private static long _lastFilePosition;

        /// <summary>
        /// Last known Z height
        /// </summary>
        private static float _lastHeight;

        /// <summary>
        /// Update the layers
        /// </summary>
        private static void UpdateLayers()
        {
            // Are we printing?
            if (Provider.Get.Job.Duration is null)
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
                Provider.Get.Job.Layers.Clear();
            }

            // Don't continue from here unless the layer number is known and valid
            if (Provider.Get.Job.Layer is null || Provider.Get.Job.Layer.Value < 0)
            {
                return;
            }

            if (Provider.Get.Job.Layer.Value > 0 && Provider.Get.Job.Layer.Value != _lastLayer)
            {
                // Compute layer usage stats first
                int numChangedLayers = (Provider.Get.Job.Layer.Value > _lastLayer) ? Math.Abs(Provider.Get.Job.Layer.Value - _lastLayer) : 1;
                int printDuration = Provider.Get.Job.Duration.Value - (Provider.Get.Job.WarmUpDuration is not null ? Provider.Get.Job.WarmUpDuration.Value : 0);
                float avgLayerDuration = (printDuration - _lastDuration) / numChangedLayers;
                List<float> totalFilamentUsage = [], avgFilamentUsage = [];
                long bytesPrinted = (Provider.Get.Job.FilePosition is not null) ? (Provider.Get.Job.FilePosition.Value - _lastFilePosition) : 0L;
                float avgFractionPrinted = (Provider.Get.Job.File.Size > 0) ? (float)bytesPrinted / (Provider.Get.Job.File.Size * numChangedLayers) : 0F;
                for (int i = 0; i < Provider.Get.Move.Extruders.Count; i++)
                {
                    if (Provider.Get.Move.Extruders[i] is not null)
                    {
                        float lastFilamentUsage = (i < _lastFilamentUsage.Count) ? _lastFilamentUsage[i] : 0F;
                        totalFilamentUsage.Add(Provider.Get.Move.Extruders[i].RawPosition);
                        avgFilamentUsage.Add((Provider.Get.Move.Extruders[i].RawPosition - lastFilamentUsage) / numChangedLayers);
                    }
                }

                // Get layer height
                float currentHeight = 0F;
                foreach (Axis axis in Provider.Get.Move.Axes)
                {
                    if (axis is { Letter: 'Z', UserPosition: {} })
                    {
                        currentHeight = axis.UserPosition.Value;
                        break;
                    }
                }
                float avgLayerHeight = Math.Abs(currentHeight - _lastHeight) / Math.Abs(Provider.Get.Job.Layer.Value - _lastLayer);

                if (Provider.Get.Job.Layer > _lastLayer)
                {
                    // Add new layers
                    for (int i = Provider.Get.Job.Layers.Count; i < Provider.Get.Job.Layer.Value - 1; i++)
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
                        foreach (AnalogSensor? sensor in Provider.Get.Sensors.Analog)
                        {
                            if (sensor is not null)
                            {
                                newLayer.Temperatures.Add(sensor.LastReading);
                            }
                        }
                        Provider.Get.Job.Layers.Add(newLayer);
                    }
                }
                else if (Provider.Get.Job.Layer < _lastLayer)
                {
                    // Layer count went down (probably printing sequentially), update the last layer
                    Layer lastLayer;
                    if (Provider.Get.Job.Layers.Count < _lastLayer)
                    {
                        lastLayer = new()
                        {
                            Height = avgLayerHeight
                        };
                        foreach (AnalogSensor? sensor in Provider.Get.Sensors.Analog)
                        {
                            if (sensor is not null)
                            {
                                lastLayer.Temperatures.Add(sensor.LastReading);
                            }
                        }
                        Provider.Get.Job.Layers.Add(lastLayer);
                    }
                    else
                    {
                        lastLayer = Provider.Get.Job.Layers[_lastLayer - 1];
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
                _lastFilePosition = Provider.Get.Job.FilePosition ?? 0L;
                _lastHeight = currentHeight;
                _lastLayer = Provider.Get.Job.Layer.Value;
            }
        }

        /// <summary>
        /// Called by the SPI subsystem when the connection to the Duet has been lost
        /// </summary>
        public static void ConnectionLost()
        {
            using (Provider.AccessReadWrite())
            {
                Provider.Get.Boards.Clear();
                Provider.Get.Global.Clear();
                if (Provider.Get.State.Status != MachineStatus.Halted && Provider.Get.State.Status != MachineStatus.Updating)
                {
                    Provider.Get.State.Status = MachineStatus.Disconnected;
                }
                Provider.Get.State.DisplayMessage = string.Empty;
                Provider.Get.State.MessageBox = null;
            }

            _lastSeqs.Clear();
        }
    }
}
