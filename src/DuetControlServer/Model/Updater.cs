using DuetAPI.Commands;
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
        private static readonly AsyncMonitor _monitor = new();

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
            using (await _monitor.EnterAsync(cancellationToken))
            {
                await _monitor.WaitAsync(cancellationToken);
                Program.CancellationToken.ThrowIfCancellationRequested();
            }
        }

        /// <summary>
        /// Process a config response (no longer supported or encouraged; for backwards-compatibility)
        /// </summary>
        /// <param name="response">Legacy config response</param>
        /// <returns>Asynchronous task</returns>
        public static async Task ProcessLegacyConfigResponse(byte[] response)
        {
            using JsonDocument jsonDocument = JsonDocument.Parse(response);
            using (await _monitor.EnterAsync(Program.CancellationToken))
            {
                if (jsonDocument.RootElement.TryGetProperty("boardName", out JsonElement boardName))
                {
                    using (await Provider.AccessReadWriteAsync())
                    {
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
                    using (await Provider.AccessReadWriteAsync())
                    {
                        Provider.Get.Boards.Add(new Board
                        {
                            IapFileNameSBC = $"Duet3_SDiap_MB6HC.bin",
                            FirmwareFileName = "Duet3Firmware_MB6HC.bin"
                        });
                    }
                    _logger.Warn("Deprecated firmware detected, assuming legacy firmware files. You may have to use bossa to update it");
                }

                // Cannot perform any further updates...
                _monitor.PulseAll();

                // Check if the firmware is supposed to be updated
                if (Settings.UpdateOnly && !_updatingFirmware)
                {
                    _updatingFirmware = true;
                    _ = Task.Run(UpdateFirmware);
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
                    // Request the limits if no sequence numbers have been set yet
                    using (await _monitor.EnterAsync(Program.CancellationToken))
                    {
                        if (_lastSeqs.IsEmpty)
                        {
                            jsonData = await SPI.Interface.RequestObjectModel("limits", "d99vn");
                            using JsonDocument limitsDocument = JsonDocument.Parse(jsonData);
                            if (limitsDocument.RootElement.TryGetProperty("key", out JsonElement limitsKey) && limitsKey.GetString().Equals("limits", StringComparison.InvariantCultureIgnoreCase) &&
                                limitsDocument.RootElement.TryGetProperty("result", out JsonElement limitsResult))
                            {
                                using (await Provider.AccessReadWriteAsync())
                                {
                                    Provider.Get.UpdateFromFirmwareModel("limits", limitsResult);
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
                    jsonData = await SPI.Interface.RequestObjectModel(string.Empty, "d99fn");
                    using JsonDocument statusDocument = JsonDocument.Parse(jsonData);
                    if (statusDocument.RootElement.TryGetProperty("key", out JsonElement statusKey) && string.IsNullOrEmpty(statusKey.GetString()) &&
                        statusDocument.RootElement.TryGetProperty("result", out JsonElement statusResult))
                    {
                        // Update frequently changing properties
                        using (await Provider.AccessReadWriteAsync())
                        {
                            Provider.Get.UpdateFromFirmwareModel(string.Empty, statusResult);
                            if (Provider.IsUpdating && Provider.Get.State.Status != MachineStatus.Updating)
                            {
                                Provider.Get.State.Status = MachineStatus.Updating;
                            }
                        }

                        // Update object model keys depending on the sequence numbers
                        foreach (JsonProperty seqProperty in statusResult.GetProperty("seqs").EnumerateObject())
                        {
                            if (seqProperty.Name != "reply" && (!Settings.UpdateOnly || seqProperty.Name == "boards") &&
                                seqProperty.Value.ValueKind == JsonValueKind.Number)
                            {
                                int newSeq = seqProperty.Value.GetInt32();
                                if (!_lastSeqs.TryGetValue(seqProperty.Name, out int lastSeq) || lastSeq != newSeq)
                                {
                                    _logger.Debug("Requesting update of key {0}, seq {1} -> {2}", seqProperty.Name, lastSeq, newSeq);

                                    int next = 0, offset = 0;
                                    do
                                    {
                                        // Request the next model chunk
                                        jsonData = await SPI.Interface.RequestObjectModel(seqProperty.Name, (next == 0) ? "d99vn" : $"d99vna{next}");
                                        using JsonDocument keyDocument = JsonDocument.Parse(jsonData);
                                        offset = next;
                                        next = keyDocument.RootElement.TryGetProperty("next", out JsonElement nextValue) ? nextValue.GetInt32() : 0;

                                        if (keyDocument.RootElement.TryGetProperty("key", out JsonElement keyName) &&
                                            keyDocument.RootElement.TryGetProperty("result", out JsonElement keyResult))
                                        {
                                            _lastSeqs[seqProperty.Name] = newSeq;
                                            using (await Provider.AccessReadWriteAsync())
                                            {
                                                if (Provider.Get.UpdateFromFirmwareModel(keyName.GetString(), keyResult, offset, next == 0))
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
                                        }
                                        else
                                        {
                                            _logger.Warn("Received invalid object model key response without key and/or result field(s)");
                                            break;
                                        }

                                        // Check the index of the next element
                                        offset = next;
                                    }
                                    while (next != 0);
                                }
                            }
                        }

                        // Update the layers
                        UpdateLayers();

                        // Object model is now up-to-date, notify waiting clients
                        using (await _monitor.EnterAsync(Program.CancellationToken))
                        {
                            _monitor.PulseAll();
                        }

                        // Check if the firmware is supposed to be updated
                        if (Settings.UpdateOnly && !_updatingFirmware)
                        {
                            _updatingFirmware = true;
                            _ = Task.Run(UpdateFirmware);
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
                catch (JsonException e)
                {
                    _logger.Error(e, "Failed to merge JSON: {0}", Encoding.UTF8.GetString(jsonData));
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
        /// Update the firmware internally
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private static async Task UpdateFirmware()
        {
            Console.Write("Updating firmware... ");
            try
            {
                Commands.Code updateCode = new()
                {
                    Type = CodeType.MCode,
                    MajorNumber = 997
                };
                await updateCode.Execute();
                Console.WriteLine("Done!");
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: {0}", e.Message);
                _logger.Debug(e);
            }
        }

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
            if (Provider.Get.Job.Duration == null || Provider.Get.Job.Layer == null)
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

            int numChangedLayers = Math.Abs(Provider.Get.Job.Layer.Value - _lastLayer);
            if (numChangedLayers > 0 && Provider.Get.Job.Layer.Value > 0 && _lastLayer > 0)
            {
                // Compute average stats per changed layer
                int printDuration = Provider.Get.Job.Duration.Value - (Provider.Get.Job.WarmUpDuration != null ? Provider.Get.Job.WarmUpDuration.Value : 0);
                float avgLayerDuration = (printDuration - _lastDuration) / numChangedLayers;
                List<float> totalFilamentUsage = new(), avgFilamentUsage = new();
                long bytesPrinted = (Provider.Get.Job.FilePosition != null) ? (Provider.Get.Job.FilePosition.Value - _lastFilePosition) : 0L;
                float avgFractionPrinted = (Provider.Get.Job.File.Size > 0) ? (float)bytesPrinted / (Provider.Get.Job.File.Size * numChangedLayers) : 0F;
                for (int i = 0; i < Provider.Get.Move.Extruders.Count; i++)
                {
                    if (Provider.Get.Move.Extruders[i] != null)
                    {
                        float lastFilamentUsage = (i < _lastFilamentUsage.Count) ? _lastFilamentUsage[i] : 0F;
                        totalFilamentUsage.Add(Provider.Get.Move.Extruders[i].RawPosition);
                        avgFilamentUsage.Add((Provider.Get.Move.Extruders[i].RawPosition - lastFilamentUsage) / numChangedLayers);
                    }
                }
                float currentHeight = 0F;
                foreach (Axis axis in Provider.Get.Move.Axes)
                {
                    if (axis != null && axis.Letter == 'Z' && axis.UserPosition != null)
                    {
                        currentHeight = axis.UserPosition.Value;
                        break;
                    }
                }
                float avgHeight = Math.Abs(currentHeight - _lastHeight) / numChangedLayers;

                // Add missing layers
                for (int i = Provider.Get.Job.Layers.Count; i < Provider.Get.Job.Layer.Value - 1; i++)
                {
                    Layer newLayer = new();
                    foreach (AnalogSensor sensor in Provider.Get.Sensors.Analog)
                    {
                        if (sensor != null)
                        {
                            newLayer.Temperatures.Add(sensor.LastReading);
                        }
                    }
                    newLayer.Height = avgHeight;
                    Provider.Get.Job.Layers.Add(newLayer);
                }

                // Merge data
                for (int i = Math.Min(_lastLayer, Provider.Get.Job.Layer.Value); i < Math.Max(_lastLayer, Provider.Get.Job.Layer.Value); i++)
                {
                    Layer layer = Provider.Get.Job.Layers[i - 1];
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
                _lastFilePosition = Provider.Get.Job.FilePosition ?? 0L;
                _lastHeight = currentHeight;
            }
            _lastLayer = Provider.Get.Job.Layer.Value;
        }

        /// <summary>
        /// Called by the SPI subsystem when the connection to the Duet has been lost
        /// </summary>
        public static void ConnectionLost()
        {
            using (Provider.AccessReadWrite())
            {
                Provider.Get.Boards.Clear();
                if (Provider.Get.State.Status != MachineStatus.Halted && Provider.Get.State.Status != MachineStatus.Updating)
                {
                    Provider.Get.State.Status = MachineStatus.Off;
                }
            }

            _lastSeqs.Clear();
        }
    }
}
