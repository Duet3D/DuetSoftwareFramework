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

            byte[] jsonData = Array.Empty<byte>();
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
                            jsonData = await SPI.Interface.RequestObjectModel("limits", "d99vno");
                            using JsonDocument limitsDocument = JsonDocument.Parse(jsonData);
                            if (limitsDocument.RootElement.TryGetProperty("key", out JsonElement limitsKey) && limitsKey.GetString()!.Equals("limits", StringComparison.InvariantCultureIgnoreCase) &&
                                limitsDocument.RootElement.TryGetProperty("result", out JsonElement limitsResult))
                            {
                                using (await Provider.AccessReadWriteAsync())
                                {
                                    Provider.Get.UpdateFromFirmwareJson("limits", limitsResult);
                                    _logger.Debug("Updated key limits");
                                }
                            }
                            else
                            {
                                _logger.Warn("Received invalid object model limits response without key and/or result field(s)");
                            }
                        }
                    }

                    // Request the next status update
                    jsonData = await SPI.Interface.RequestObjectModel(string.Empty, "d99fno");
                    using JsonDocument statusDocument = JsonDocument.Parse(jsonData);
                    if (statusDocument.RootElement.TryGetProperty("key", out JsonElement statusKey) &&
                        statusDocument.RootElement.TryGetProperty("result", out JsonElement statusResult))
                    {
                        // Update frequently changing properties
                        using (await Provider.AccessReadWriteAsync())
                        {
                            Provider.Get.UpdateFromFirmwareJson(string.Empty, statusResult);
                            if (Provider.IsUpdating && Provider.Get.State.Status != MachineStatus.Updating)
                            {
                                Provider.Get.State.Status = MachineStatus.Updating;
                            }
                            UpdateLayers();
                        }

                        // Update object model keys depending on the sequence numbers
                        foreach (JsonProperty seqProperty in statusResult.GetProperty("seqs").EnumerateObject())
                        {
                            if (seqProperty.Name != "reply" && seqProperty.Value.ValueKind == JsonValueKind.Number &&
                                (!Settings.UpdateOnly || seqProperty.Name is "boards" or "directories" or "state"))
                            {
                                int newSeq = seqProperty.Value.GetInt32();
                                if (!_lastSeqs.TryGetValue(seqProperty.Name, out int lastSeq) || lastSeq != newSeq)
                                {
                                    _logger.Debug("Requesting update of key {0}, seq {1} -> {2}", seqProperty.Name, lastSeq, newSeq);

                                    int next = 0;
                                    do
                                    {
                                        // Request the next model chunk
                                        jsonData = await SPI.Interface.RequestObjectModel(seqProperty.Name, (next == 0) ? "d99vno" : $"d99vnoa{next}");
                                        using JsonDocument keyDocument = JsonDocument.Parse(jsonData);
                                        int offset = next;
                                        next = keyDocument.RootElement.TryGetProperty("next", out JsonElement nextValue) ? nextValue.GetInt32() : 0;

                                        if (keyDocument.RootElement.TryGetProperty("key", out JsonElement keyName) &&
                                            keyDocument.RootElement.TryGetProperty("result", out JsonElement keyResult))
                                        {
                                            _lastSeqs[seqProperty.Name] = newSeq;
                                            using (await Provider.AccessReadWriteAsync())
                                            {
                                                if (Provider.Get.UpdateFromFirmwareJson(keyName.GetString(), keyResult, offset, next == 0))
                                                {
                                                    _logger.Debug("Updated key {0}{1}", keyName.GetString(), (offset + next != 0) ? $" starting from {offset}, next {next}" : string.Empty);
                                                    if (_logger.IsTraceEnabled)
                                                    {
                                                        _logger.Trace("Key JSON: {0}", keyResult.ToString());
                                                    }
                                                }
                                                else
                                                {
                                                    _logger.Warn($"Invalid key {keyName.GetString()} in the object model");
                                                    break;
                                                }

                                                if (Provider.IsUpdating && Provider.Get.State.Status != MachineStatus.Updating)
                                                {
                                                    Provider.Get.State.Status = MachineStatus.Updating;
                                                }
                                            }

                                            // move.axes requires special querying if it exceeds 9 items
                                            if (keyName.GetString() == "move" && keyResult.TryGetProperty("axes", out JsonElement moveAxes) && moveAxes.GetArrayLength() >= 9)
                                            {
                                                int nextAxis = moveAxes.GetArrayLength(), axisOffset = 0;
                                                do
                                                {
                                                    jsonData = await SPI.Interface.RequestObjectModel("move.axes", $"d99vnoa{nextAxis}");
                                                    using JsonDocument moveAxesDocument = JsonDocument.Parse(jsonData);
                                                    axisOffset = nextAxis;
                                                    nextAxis = moveAxesDocument.RootElement.TryGetProperty("next", out JsonElement nextAxisValue) ? nextAxisValue.GetInt32() : 0;

                                                    if (moveAxesDocument.RootElement.TryGetProperty("result", out JsonElement moveAxesResult))
                                                    {
                                                        using (await Provider.AccessReadWriteAsync())
                                                        {
                                                            Provider.Get.Move.Axes.UpdateFromJson(moveAxesResult, false, axisOffset, nextAxis == 0);
                                                        }
                                                    }
                                                }
                                                while (nextAxis != 0);
                                            }
                                        }
                                        else
                                        {
                                            _logger.Warn("Received invalid object model key response without key and/or result field(s)");
                                            break;
                                        }
                                    }
                                    while (next != 0);
                                }
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
                    else
                    {
                        _logger.Warn("Received invalid object model response without key and/or result field(s)");
                    }
                }
                catch (InvalidOperationException e)
                {
                    _logger.Error(e, "Failed to merge JSON due to internal error: {0}", Encoding.UTF8.GetString(jsonData));
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
        private static List<float> _lastFilamentUsage = new();

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
                List<float> totalFilamentUsage = new(), avgFilamentUsage = new();
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
