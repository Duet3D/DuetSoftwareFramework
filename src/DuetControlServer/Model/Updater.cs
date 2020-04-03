using DuetAPI.Machine;
using Nito.AsyncEx;
using System;
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
                                        IapFileNameSBC = $"Duet3iap_spi.bin",
                                        FirmwareFileName = "Duet3Firmware.bin"
                                    });
                                }
                                _logger.Warn("Deprecated firmware detected, assuming legacy firmware files. You may have to use bossa to update it");
                            }

                            // Cannot perform any further updates...
                            _fullyUpdated.NotifyAll();
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
                                }

                                // Request object model updates wherever needed
                                bool objectModelSynchronized = true;
                                foreach (JsonProperty seqProperty in result.GetProperty("seqs").EnumerateObject())
                                {
                                    if (seqProperty.Name != "reply")
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

                                if (objectModelSynchronized)
                                {
                                    // Notify clients waiting for the machine model to be synchronized
                                    _fullyUpdated.NotifyAll();
                                }
                            }
                            else
                            {
                                // Specific request - still updating the OM
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
                                    }
                                    else
                                    {
                                        _logger.Warn($"Invalid key {key.GetString()} in the object model");
                                    }
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
        /// Called when the connection to the Duet has been lost
        /// </summary>
        /// <param name="errorMessage">Message of the error that led to this event</param>
        public static void ConnectionLost(string errorMessage)
        {
            using (Provider.AccessReadWrite())
            {
                Provider.Get.Boards.Clear();
                Provider.Get.State.Status = MachineStatus.Off;
            }

            using (_lock.Lock())
            {
                _lastSeqs.Clear();
            }

            _ = Utility.Logger.LogOutput(MessageType.Warning, $"Lost connection to Duet ({errorMessage})");
        }
    }
}
