using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using Nito.AsyncEx;
using System;
using System.Collections.Concurrent;
using System.Linq;
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
        private static readonly AsyncMonitor _monitor = new AsyncMonitor();

        /// <summary>
        /// Dictionary of main keys vs last sequence numbers
        /// </summary>
        private static readonly ConcurrentDictionary<string, int> _lastSeqs = new ConcurrentDictionary<string, int>();

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
                Program.CancelSource.Token.ThrowIfCancellationRequested();
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
                        if (_lastSeqs.Count == 0)
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
                            if (seqProperty.Name != "reply" && (!Settings.UpdateOnly || seqProperty.Name == "boards"))
                            {
                                int newSeq = seqProperty.Value.GetInt32();
                                if (!_lastSeqs.TryGetValue(seqProperty.Name, out int lastSeq) || lastSeq != newSeq)
                                {
                                    _logger.Debug("Requesting update of key {0}, seq {1} -> {2}", seqProperty.Name, lastSeq, newSeq);

                                    jsonData = await SPI.Interface.RequestObjectModel(seqProperty.Name, "d99vn");
                                    using JsonDocument keyDocument = JsonDocument.Parse(jsonData);
                                    if (keyDocument.RootElement.TryGetProperty("key", out JsonElement keyName) &&
                                        keyDocument.RootElement.TryGetProperty("result", out JsonElement keyResult))
                                    {
                                        _lastSeqs[seqProperty.Name] = newSeq;
                                        using (await Provider.AccessReadWriteAsync())
                                        {
                                            if (Provider.Get.UpdateFromFirmwareModel(keyName.GetString(), keyResult))
                                            {
                                                _logger.Debug("Updated key {0}", keyName.GetString());
                                                if (_logger.IsTraceEnabled)
                                                {
                                                    _logger.Trace("Key JSON: {0}", keyResult.ToString());
                                                }
                                            }
                                            else
                                            {
                                                _logger.Warn($"Invalid key {keyName.GetString()} in the object model");
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
                                    }
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
                await Task.Delay(Settings.ModelUpdateInterval);
            }
            while (!Program.CancelSource.IsCancellationRequested);
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
                Commands.Code updateCode = new Commands.Code
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
        /// Update the layers
        /// </summary>
        private static void UpdateLayers()
        {
            if (Provider.Get.State.Status != MachineStatus.Processing && Provider.Get.State.Status != MachineStatus.Simulating)
            {
                // Don't do anything if no print is in progress
                return;
            }

            // Check if the last layer is complete
            if (Provider.Get.Job.Layer > Provider.Get.Job.Layers.Count + 1)
            {
                float[] extrRaw = (from extruder in Provider.Get.Move.Extruders
                                   where extruder != null
                                   select extruder.RawPosition).ToArray();
                float fractionPrinted = (float)((double)Provider.Get.Job.FilePosition / Provider.Get.Job.File.Size);
                float currentHeight = Provider.Get.Move.Axes.FirstOrDefault(axis => axis.Letter == 'Z').UserPosition ?? 0F;

                float lastHeight = 0F, lastProgress = 0F;
                int lastDuration = 0;
                float[] lastFilamentUsage = new float[extrRaw.Length];
                foreach (Layer l in Provider.Get.Job.Layers)
                {
                    lastHeight += l.Height;
                    lastDuration += l.Duration;
                    lastProgress += l.FractionPrinted;
                    for (int i = 0; i < Math.Min(lastFilamentUsage.Length, l.Filament.Count); i++)
                    {
                        lastFilamentUsage[i] += l.Filament[i];
                    }
                }

                float[] filamentUsage = new float[extrRaw.Length];
                for (int i = 0; i < filamentUsage.Length; i++)
                {
                    filamentUsage[i] = extrRaw[i] - lastFilamentUsage[i];
                }

                int printDuration = Provider.Get.Job.Duration.Value - Provider.Get.Job.WarmUpDuration.Value;
                Layer layer = new Layer
                {
                    Duration = printDuration - lastDuration,
                    FractionPrinted = fractionPrinted - lastProgress,
                    Height = (Provider.Get.Job.Layer > 2) ? currentHeight - lastHeight : Provider.Get.Job.File.FirstLayerHeight
                };
                foreach (float filamentItem in filamentUsage)
                {
                    layer.Filament.Add(filamentItem);
                }

                Provider.Get.Job.Layers.Add(layer);
            }
            else if (Provider.Get.Job.Layer < Provider.Get.Job.Layers.Count)
            {
                // Starting a new print job, clear the layers
                Provider.Get.Job.Layers.Clear();
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
                if (Provider.Get.State.Status != MachineStatus.Halted && Provider.Get.State.Status != MachineStatus.Updating)
                {
                    Provider.Get.State.Status = MachineStatus.Off;
                }
            }

            _lastSeqs.Clear();
        }
    }
}
