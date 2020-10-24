using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
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
        private static readonly AsyncLock _lock = new AsyncLock();

        /// <summary>
        /// Condition variable to trigger when new JSON data is available
        /// </summary>
        private static readonly AsyncConditionVariable _dataAvailable = new AsyncConditionVariable(_lock);

        /// <summary>
        /// Condition variable to trigger when the machine model has been fully updated
        /// </summary>
        private static readonly AsyncConditionVariable _fullyUpdated = new AsyncConditionVariable(_lock);

        /// <summary>
        /// Dictionary of main keys vs last sequence numbers
        /// </summary>
        private static readonly Dictionary<string, int> _lastSeqs = new Dictionary<string, int>();

        /// <summary>
        /// UTF-8 representation of the received object model data
        /// </summary>
        private static readonly byte[] _json = new byte[SPI.Communication.Consts.BufferSize];

        /// <summary>
        /// Length of the received object model data
        /// </summary>
        private static int _jsonLength = 0;

        /// <summary>
        /// Wait for the model to be fully updated from RepRapFirmware
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public static async Task WaitForFullUpdate(CancellationToken cancellationToken)
        {
            using (await _lock.LockAsync(cancellationToken))
            {
                await _fullyUpdated.WaitAsync(cancellationToken);
                Program.CancelSource.Token.ThrowIfCancellationRequested();
            }
        }

        /// <summary>
        /// Merge a received object model response
        /// </summary>
        /// <param name="json">JSON data</param>
        public static void ProcessResponse(ReadOnlySpan<byte> json)
        {
            using (_lock.Lock(Program.CancellationToken))
            {
                json.CopyTo(_json);
                _jsonLength = json.Length;
                _dataAvailable.NotifyAll();
            }
        }

        /// <summary>
        /// Process status updates in the background
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Run()
        {
            do
            {
                using (await _lock.LockAsync(Program.CancellationToken))
                {
                    // Wait for the next object model update
                    await _dataAvailable.WaitAsync(Program.CancellationToken);
                    Program.CancellationToken.ThrowIfCancellationRequested();

                    // Process it
                    try
                    {
                        ReadOnlyMemory<byte> json = new ReadOnlyMemory<byte>(_json, 0, _jsonLength);
                        using JsonDocument jsonDocument = JsonDocument.Parse(json);
                        if (SPI.DataTransfer.ProtocolVersion == 1)
                        {
                            // This must be a legacy config response used to get the board names
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
                            _fullyUpdated.NotifyAll();

                            // Check if the firmware is supposed to be updated
                            if (Settings.UpdateOnly && !_updatingFirmware)
                            {
                                _updatingFirmware = true;
                                _ = Task.Run(UpdateFirmware);
                            }
                        }
                        else if (jsonDocument.RootElement.TryGetProperty("key", out JsonElement key) &&
                                 jsonDocument.RootElement.TryGetProperty("result", out JsonElement result))
                        {
                            // This is a new object model result
                            if (string.IsNullOrWhiteSpace(key.GetString()))
                            {
                                // Standard request to update frequently changing fields
                                using (await Provider.AccessReadWriteAsync())
                                {
                                    Provider.Get.UpdateFromFirmwareModel(string.Empty, result);
                                    if (Provider.IsUpdating && Provider.Get.State.Status != MachineStatus.Updating)
                                    {
                                        Provider.Get.State.Status = MachineStatus.Updating;
                                    }
                                }

                                // Request limits if no sequence numbers have been set yet
                                if (_lastSeqs.Count == 0)
                                {
                                    SPI.Interface.RequestObjectModel("limits", "d99vn");
                                }

                                // Request object model updates wherever needed
                                bool objectModelSynchronized = true;
                                foreach (JsonProperty seqProperty in result.GetProperty("seqs").EnumerateObject())
                                {
                                    if (seqProperty.Name != "reply" && (!Settings.UpdateOnly || seqProperty.Name == "boards"))
                                    {
                                        int newSeq = seqProperty.Value.GetInt32();
                                        if (!_lastSeqs.TryGetValue(seqProperty.Name, out int lastSeq) || lastSeq != newSeq)
                                        {
                                            _logger.Debug("Requesting update of key {0}, seq {1} -> {2}", seqProperty.Name, lastSeq, newSeq);
                                            _lastSeqs[seqProperty.Name] = newSeq;
                                            SPI.Interface.RequestObjectModel(seqProperty.Name, "d99vn");
                                            objectModelSynchronized = false;
                                        }
                                    }
                                }

                                // Update the layers
                                UpdateLayers();

                                if (objectModelSynchronized)
                                {
                                    // Notify clients waiting for the machine model to be synchronized
                                    _fullyUpdated.NotifyAll();

                                    // Check if the firmware is supposed to be updated
                                    if (Settings.UpdateOnly && !_updatingFirmware)
                                    {
                                        _updatingFirmware = true;
                                        _ = Task.Run(UpdateFirmware);
                                    }
                                }
                            }
                            else
                            {
                                // Specific request - still updating the OM
                                bool outputBoards = false;
                                using (await Provider.AccessReadWriteAsync())
                                {
                                    string keyName = key.GetString();
                                    if (Provider.Get.UpdateFromFirmwareModel(key.GetString(), result))
                                    {
                                        _logger.Debug("Updated key {0}", keyName);
                                        if (_logger.IsTraceEnabled)
                                        {
                                            _logger.Trace("Key JSON: {0}", Encoding.UTF8.GetString(_json, 0, _jsonLength));
                                        }

                                        if (keyName == "boards" && Provider.Get.Boards.Count > 0 && string.IsNullOrEmpty(Provider.Get.Boards[0].IapFileNameSBC))
                                        {
                                            outputBoards = true;
                                        }
                                    }
                                    else
                                    {
                                        _logger.Warn($"Invalid key {key.GetString()} in the object model");
                                    }

                                    if (Provider.IsUpdating && Provider.Get.State.Status != MachineStatus.Updating)
                                    {
                                        Provider.Get.State.Status = MachineStatus.Updating;
                                    }
                                }

                                if (outputBoards)
                                {
#warning This should never be triggered but both dc42 and I had an issue (at least once) where boards[0].*File was not set
                                    await Provider.Output(MessageType.Warning, $"Received malformed boards key: '{Encoding.UTF8.GetString(_json, 0, _jsonLength)}'");
                                }
                            }
                        }
                        else
                        {
                            _logger.Warn("Received invalid object model response without key and/or result field(s)");
                        }
                    }
                    catch (InvalidOperationException e)
                    {
                        _logger.Error(e, "Failed to merge JSON due to internal error: {0}", Encoding.UTF8.GetString(_json, 0, _jsonLength));
                    }
                    catch (JsonException e)
                    {
                        _logger.Error(e, "Failed to merge JSON: {0}", Encoding.UTF8.GetString(_json, 0, _jsonLength));
                    }
                }
            }
            while (true);
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
            finally
            {
                Program.CancelSource.Cancel();
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
        /// <param name="errorMessage">Optional error that led to this event</param>
        public static void ConnectionLost(string errorMessage = null)
        {
            using (Provider.AccessReadWrite())
            {
                Provider.Get.Boards.Clear();
                Provider.Get.Move.Compensation.File = null;
                if (Provider.Get.State.Status != MachineStatus.Halted && Provider.Get.State.Status != MachineStatus.Updating)
                {
                    Provider.Get.State.Status = MachineStatus.Off;
                }
            }

            using (_lock.Lock(Program.CancellationToken))
            {
                // Query the full object model when a connection can be established again
                _lastSeqs.Clear();
            }

            if (!string.IsNullOrEmpty(errorMessage))
            {
                _ = Utility.Logger.LogOutput(MessageType.Warning, $"Lost connection to Duet ({errorMessage})");
            }
        }
    }
}
