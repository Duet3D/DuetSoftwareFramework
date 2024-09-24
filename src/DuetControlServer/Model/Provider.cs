using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Utility;
using Nito.AsyncEx;

namespace DuetControlServer.Model
{
    /// <summary>
    /// Provider for the machine's object model to provides thread-safe read/write access.
    /// Make sure to access the machine model only when atomic operations are performed
    /// so that pending updates can be performed as quickly as possible.
    /// </summary>
    public static class Provider
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Wrapper around the lock which notifies subscribers whenever an update has been processed.
        /// It is also able to detect the origin of model-related deadlocks
        /// </summary>
        private sealed class LockWrapper : IDisposable
        {
            /// <summary>
            /// Internal lock
            /// </summary>
            private readonly IDisposable _lock;

            /// <summary>
            /// Indicates if this lock is meant for write access
            /// </summary>
            private readonly bool _isWriteLock;

            /// <summary>
            /// CTS to trigger when the lock is being released
            /// </summary>
            private readonly CancellationTokenSource? _releaseCts;

            /// <summary>
            /// Constructor of the lock wrapper
            /// </summary>
            /// <param name="lockItem">Actual lock</param>
            /// <param name="isWriteLock">Whether the lock is a read/write lock</param>
            internal LockWrapper(IDisposable lockItem, bool isWriteLock)
            {
                _lock = lockItem;
                _isWriteLock = isWriteLock;

                if (Settings.MaxMachineModelLockTime > 0)
                {
                    _releaseCts = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);

                    StackTrace stackTrace = new(true);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(Settings.MaxMachineModelLockTime, _releaseCts.Token);
                            _logger.Fatal("{0} deadlock detected, stack trace of the deadlock:\n{1}", isWriteLock ? "Writer" : "Reader", stackTrace);
                            await Program.ShutdownAsync();
                        }
                        finally
                        {
                            _releaseCts.Dispose();
                        }
                    });
                }
            }

            /// <summary>
            /// Dispose method that is called when the lock is released
            /// </summary>
            public void Dispose()
            {
                try
                {
                    if (_isWriteLock)
                    {
                        // It is safe to assume that the object model has been updated
                        using (_updateLock.Lock(Program.CancellationToken))
                        {
                            _updateEvent.NotifyAll();
                        }

                        // Clear the messages again if waiting clients could output this message
                        if (IPC.Processors.CodeStream.HasClientsWaitingForMessages || IPC.Processors.ModelSubscription.HasClientsWaitingForMessages)
                        {
                            Get.Messages.Clear();
                        }
                    }
                }
                finally
                {
                    // Dispose the lock again
                    _lock.Dispose();

                    // Stop the deadlock detection task if applicable
                    _releaseCts?.Cancel();
                }
            }
        }

        /// <summary>
        /// Lock for read/write access
        /// </summary>
        private static readonly AsyncReaderWriterLock _readWriteLock = new();

        /// <summary>
        /// Base lock for update conditions
        /// </summary>
        private static readonly AsyncLock _updateLock = new();

        /// <summary>
        /// Condition variable to trigger when the machine model has been updated
        /// </summary>
        private static readonly AsyncConditionVariable _updateEvent = new(_updateLock);

        /// <summary>
        /// Get the machine model. Make sure to call the acquire the corresponding lock first!
        /// </summary>
        /// <returns>Current Duet machine object model</returns>
        /// <seealso cref="AccessReadOnlyAsync()"/>
        /// <seealso cref="AccessReadWriteAsync()"/>
        /// <seealso cref="WaitForUpdate(CancellationToken)"/>
        /// <seealso cref="Updater.WaitForFullUpdate(CancellationToken)"/>
        public static ObjectModel Get { get; } = new();

        /// <summary>
        /// Configured password (see M551)
        /// </summary>
        public static string Password { get; set; } = DuetAPI.Connection.Defaults.Password;

        /// <summary>
        /// Whether the current machine status is overridden because an update is in progress
        /// </summary>
        public static bool IsUpdating
        {
            get => _isUpdating;
            set
            {
                if (value)
                {
                    Get.State.Status = MachineStatus.Updating;
                }
                _isUpdating = value;
            }
        }
        private static bool _isUpdating;

        /// <summary>
        /// Dictionary of the properties vs. sender type + JSON content that failed to be deserialized
        /// </summary>
        private static readonly Dictionary<Type, Tuple<Type, JsonElement>> _deserializationErrors = new();

        /// <summary>
        /// Event handler to be called when the deserialization of a property failed
        /// </summary>
        /// <param name="sender">Object that failed to deserialze a property</param>
        /// <param name="e">Event args pointing to the property that failed to be deserialized</param>
        private static void OnDeserializationFailed(object sender, DeserializationFailedEventArgs e)
        {
            if (!_deserializationErrors.ContainsKey(e.TargetType))
            {
                lock (_deserializationErrors)
                {
                    _deserializationErrors.Add(e.TargetType, new(sender.GetType(), e.JsonValue));
                }
                _logger.Error("Failed to deserialize {0} -> {1} from {2}", sender.GetType().Name, e.TargetType.Name, e.JsonValue.GetRawText());
            }
        }

        /// <summary>
        /// Initialize the object model provider with values that are not expected to change
        /// </summary>
        public static void Init()
        {
            ObjectModel.OnDeserializationFailed += OnDeserializationFailed;
            BuildDateTimeAttribute buildAttribute = (BuildDateTimeAttribute)Attribute.GetCustomAttribute(System.Reflection.Assembly.GetExecutingAssembly(), typeof(BuildDateTimeAttribute))!;
            Get.SBC = new()
            {
                AppArmor = Directory.Exists("/sys/module/apparmor"),
                Distribution = GetDistribution(),
                DistributionBuildTime = GetDistributionBuildTime()
            };
            Get.SBC.CPU.Hardware = GetCpuHardware();
            Get.SBC.CPU.NumCores = GetCpuNumCores();
            Get.SBC.DSF.BuildDateTime = buildAttribute.Date ?? "unknown build time";
            Get.SBC.DSF.Is64Bit = Environment.Is64BitProcess;
            Get.SBC.DSF.Version = Program.Version;
            Get.SBC.DSF.PluginSupport = Settings.PluginSupport;
            Get.SBC.DSF.RootPluginSupport = Settings.PluginSupport && Settings.RootPluginSupport;
            Get.SBC.Memory.Total = GetTotalMemory();
            Get.SBC.Model = GetSbcModel();
            Get.SBC.Serial = GetSbcSerial();
            Get.Network.Hostname = Environment.MachineName;
            Get.Network.Name = Environment.MachineName;
        }

        /// <summary>
        /// Get the CPU hardware
        /// </summary>
        /// <returns>CPU hardware or null if unknown</returns>
        public static string? GetCpuHardware()
        {
            try
            {
                Regex hardwareRegex = new(@"^Hardware\s*:\s*(\w+)", RegexOptions.IgnoreCase);
                IEnumerable<string> procInfo = File.ReadLines("/proc/cpuinfo");
                foreach (string line in procInfo)
                {
                    Match hardwareMatch = hardwareRegex.Match(line);
                    if (hardwareMatch.Success)
                    {
                        return hardwareMatch.Groups[1].Value;
                    }
                }
            }
            catch (Exception e)
            {
                _logger.Warn(e, "Failed to get CPU hardware");
            }
            return null;
        }

        /// <summary>
        /// Get the number of processor cores/threads
        /// </summary>
        /// <returns>Number of cores/threads or 1 if unknown</returns>
        public static int GetCpuNumCores()
        {
            try
            {
                Regex cpuIndexRegex = new(@"^cpu\d", RegexOptions.IgnoreCase);
                IEnumerable<string> procInfo = File.ReadLines("/proc/stat");

                int numCores = 0;
                foreach (string line in procInfo)
                {
                    if (cpuIndexRegex.IsMatch(line))
                    {
                        numCores++;
                    }
                }
                return Math.Max(numCores, 1);
            }
            catch (Exception e)
            {
                _logger.Warn(e, "Failed to get number of CPU cores");
            }
            return 1;
        }

        /// <summary>
        /// Get the current Linux distribution
        /// </summary>
        /// <returns>Distribution name or null if unknown</returns>
        public static string? GetDistribution()
        {
            if (File.Exists("/etc/os-release"))
            {
                try
                {
                    IEnumerable<string> osReleaseLines = File.ReadAllLines("/etc/os-release");
                    foreach (string line in osReleaseLines)
                    {
                        if (line.StartsWith("PRETTY_NAME="))
                        {
                            return line["PRETTY_NAME=".Length..].Trim('"', '\'');
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.Warn(e, "Failed to get distribution");
                }
            }
            return null;
        }

        /// <summary>
        /// Get the SBC model name
        /// </summary>
        /// <returns>SBC model or null if unknown</returns>
        public static string? GetSbcModel()
        {
            try
            {
                Regex modelRegex = new(@"^Model\s*:\s*(.+)", RegexOptions.IgnoreCase);
                IEnumerable<string> procInfo = File.ReadLines("/proc/cpuinfo");
                foreach (string line in procInfo)
                {
                    Match modelMatch = modelRegex.Match(line);
                    if (modelMatch.Success)
                    {
                        return modelMatch.Groups[1].Value;
                    }
                }
            }
            catch (Exception e)
            {
                _logger.Warn(e, "Failed to get SBC model");
            }
            return null;
        }

        /// <summary>
        /// Get the SBC serial
        /// </summary>
        /// <returns>SBC model or null if unknown</returns>
        public static string? GetSbcSerial()
        {
            try
            {
                Regex modelRegex = new(@"^Serial\s*:\s*(\w+)", RegexOptions.IgnoreCase);
                IEnumerable<string> procInfo = File.ReadLines("/proc/cpuinfo");
                foreach (string line in procInfo)
                {
                    Match modelMatch = modelRegex.Match(line);
                    if (modelMatch.Success)
                    {
                        return modelMatch.Groups[1].Value;
                    }
                }
            }
            catch (Exception e)
            {
                _logger.Warn(e, "Failed to get SBC serial");
            }
            return null;
        }

        /// <summary>
        /// Determine when the current Linux distribution was built
        /// </summary>
        /// <returns>Build datetime or null if unknown</returns>
        public static DateTime? GetDistributionBuildTime()
        {
            if (File.Exists("/etc/os-release"))
            {
                try
                {
                    return File.GetCreationTime("/etc/os-release");
                }
                catch (Exception e)
                {
                    _logger.Warn(e, "Failed to get distribution build time");
                }
            }
            return null;
        }

        /// <summary>
        /// Get the total memory of this SBC
        /// </summary>
        /// <returns></returns>
        public static long? GetTotalMemory()
        {
            if (File.Exists("/proc/meminfo"))
            {
                try
                {
                    Regex totalMemoryRegex = new(@"^MemTotal:\s*(\d+)( kB| KiB)", RegexOptions.IgnoreCase);
                    IEnumerable<string> memoryInfo = File.ReadAllLines("/proc/meminfo");
                    foreach (string line in memoryInfo)
                    {
                        Match totalMemoryMatch = totalMemoryRegex.Match(line);
                        if (totalMemoryMatch.Success)
                        {
                            long totalMemory = long.Parse(totalMemoryMatch.Groups[1].Value);
                            return (totalMemoryMatch.Groups.Count > 2) ? totalMemory * 1024 : totalMemory;
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.Warn(e, "Failed to get distribution build time");
                }
            }
            return null;
        }

        /// <summary>
        /// Access the machine model for read operations only
        /// </summary>
        /// <returns>Disposable lock object to be used with a using directive</returns>
        public static IDisposable AccessReadOnly(CancellationToken cancellationToken)
        {
            return new LockWrapper(_readWriteLock.ReaderLock(cancellationToken), false);
        }

        /// <summary>
        /// Access the machine model for read operations only
        /// </summary>
        /// <returns>Disposable lock object to be used with a using directive</returns>
        public static IDisposable AccessReadOnly() => AccessReadOnly(Program.CancellationToken);

        /// <summary>
        /// Access the machine model for read/write operations
        /// </summary>
        /// <returns>Disposable lock object to be used with a using directive</returns>
        public static IDisposable AccessReadWrite(CancellationToken cancellationToken)
        {
            return new LockWrapper(_readWriteLock.WriterLock(cancellationToken), true);
        }

        /// <summary>
        /// Access the machine model for read/write operations
        /// </summary>
        /// <returns>Disposable lock object to be used with a using directive</returns>
        public static IDisposable AccessReadWrite() => AccessReadWrite(Program.CancellationToken);

        /// <summary>
        /// Access the machine model asynchronously for read operations only
        /// </summary>
        /// <returns>Disposable lock object to be used with a using directive</returns>
        public static async Task<IDisposable> AccessReadOnlyAsync(CancellationToken cancellationToken)
        {
            return new LockWrapper(await _readWriteLock.ReaderLockAsync(cancellationToken), false);
        }

        /// <summary>
        /// Access the machine model asynchronously for read operations only
        /// </summary>
        /// <returns>Disposable lock object to be used with a using directive</returns>
        public static Task<IDisposable> AccessReadOnlyAsync() => AccessReadOnlyAsync(Program.CancellationToken);

        /// <summary>
        /// Access the machine model asynchronously for read/write operations
        /// </summary>
        /// <returns>Disposable lock object to be used with a using directive</returns>
        public static async Task<IDisposable> AccessReadWriteAsync(CancellationToken cancellationToken)
        {
            return new LockWrapper(await _readWriteLock.WriterLockAsync(cancellationToken), true);
        }

        /// <summary>
        /// Access the machine model asynchronously for read/write operations
        /// </summary>
        /// <returns>Disposable lock object to be used with a using directive</returns>
        public static Task<IDisposable> AccessReadWriteAsync() => AccessReadWriteAsync(Program.CancellationToken);

        /// <summary>
        /// Wait for an update to occur
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        public static void WaitForUpdate(CancellationToken cancellationToken)
        {
            using (_updateLock.Lock(cancellationToken))
            {
                _updateEvent.Wait(cancellationToken);
                Program.CancellationToken.ThrowIfCancellationRequested();
            }
        }

        /// <summary>
        /// Wait for an update to occur asynchronously
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public static async Task WaitForUpdateAsync(CancellationToken cancellationToken)
        {
            using (await _updateLock.LockAsync(cancellationToken))
            {
                await _updateEvent.WaitAsync(cancellationToken);
                Program.CancellationToken.ThrowIfCancellationRequested();
            }
        }

        /// <summary>
        /// Wait for an update to occur
        /// </summary>
        public static void WaitForUpdate() => WaitForUpdate(Program.CancellationToken);

        /// <summary>
        /// Wait for an update to occur asynchronously
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static Task WaitForUpdateAsync() => WaitForUpdateAsync(Program.CancellationToken);

        /// <summary>
        /// Indicates how many config files are being processed
        /// </summary>
        private static volatile int _numRunningConfigFiles = 0;

        /// <summary>
        /// Flag asynchronously that a start-up file is being executed. Must be called WITHOUT locking this instance first!
        /// </summary>
        /// <param name="v"></param>
        /// <returns></returns>
        public static void SetExecutingConfig(bool executing)
        {
            if (executing)
            {
                _numRunningConfigFiles++;
            }
            else
            {
                _numRunningConfigFiles--;
            }
        }

        /// <summary>
        /// Handle a macro file error asynchronously. Must be called WITHOUT locking this instance first!
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="lineNumber"></param>
        /// <param name="message"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public static async Task HandleMacroErrorAsync(string fileName, long lineNumber, string message)
        {
            string shortFileName = Path.GetFileName(fileName);
            using (await AccessReadWriteAsync())
            {
                if (_numRunningConfigFiles > 0 && Get.State.StartupError == null)
                {
                    Get.State.StartupError = new()
                    {
                        File = shortFileName,
                        Line = lineNumber,
                        Message = message
                    };
                }
            }
        }

        /// <summary>
        /// Output a generic message
        /// </summary>
        /// <param name="level">Log level</param>
        /// <param name="message">Message to output</param>
        /// <returns>Whether the message has been written</returns>
        public static bool Output(LogLevel level, Message message)
        {
            if (!string.IsNullOrWhiteSpace(message?.Content))
            {
                using (AccessReadWrite())
                {
                    // Can we output this message?
                    if (Get.State.LogLevel == LogLevel.Off || (byte)Get.State.LogLevel + (byte)level < 3)
                    {
                        return false;
                    }

                    // Print the message to the DCS log
                    switch (message.Type)
                    {
                        case MessageType.Error:
                            _logger.Error(message.Content);
                            break;
                        case MessageType.Warning:
                            _logger.Warn(message.Content);
                            break;
                        default:
                            _logger.Info(message.Content);
                            break;
                    }

                    // Send it to the object model
                    Get.Messages.Add(message);
                }

                return true;
            }
            return false;
        }

        /// <summary>
        /// Output a generic message asynchronously
        /// </summary>
        /// <param name="level">Log level</param>
        /// <param name="message">Message to output</param>
        /// <returns>Whether the message has been written</returns>
        public static async Task<bool> OutputAsync(LogLevel level, Message message)
        {
            if (!string.IsNullOrWhiteSpace(message?.Content))
            {
                using (await AccessReadWriteAsync())
                {
                    // Can we output this message?
                    if (Get.State.LogLevel == LogLevel.Off || (byte)Get.State.LogLevel + (byte)level < 3)
                    {
                        return false;
                    }

                    // Print the message to the DCS log
                    switch (message.Type)
                    {
                        case MessageType.Error:
                            _logger.Error(message.Content);
                            break;
                        case MessageType.Warning:
                            _logger.Warn(message.Content);
                            break;
                        default:
                            _logger.Info(message.Content);
                            break;
                    }

                    // Send it to the object model
                    Get.Messages.Add(message);
                }

                return true;
            }
            return false;
        }

        /// <summary>
        /// Output a generic message
        /// </summary>
        /// <param name="message">Message to output</param>
        /// <returns>Asynchronous task</returns>
        public static void Output(Message message)
        {
            if (!string.IsNullOrWhiteSpace(message?.Content))
            {
                // Print the message to the DCS log
                switch (message.Type)
                {
                    case MessageType.Error:
                        _logger.Error(message.Content);
                        break;
                    case MessageType.Warning:
                        _logger.Warn(message.Content);
                        break;
                    default:
                        _logger.Info(message.Content);
                        break;
                }

                // Send it to the object model
                using (AccessReadWrite())
                {
                    Get.Messages.Add(message);
                }
            }
        }

        /// <summary>
        /// Output a generic message asynchronously
        /// </summary>
        /// <param name="message">Message to output</param>
        /// <returns>Asynchronous task</returns>
        public static async Task OutputAsync(Message message)
        {
            if (!string.IsNullOrWhiteSpace(message?.Content))
            {
                // Print the message to the DCS log
                switch (message.Type)
                {
                    case MessageType.Error:
                        _logger.Error(message.Content);
                        break;
                    case MessageType.Warning:
                        _logger.Warn(message.Content);
                        break;
                    default:
                        _logger.Info(message.Content);
                        break;
                }

                // Send it to the object model
                using (await AccessReadWriteAsync())
                {
                    Get.Messages.Add(message);
                }
            }
        }

        /// <summary>
        /// Output a generic message
        /// </summary>
        /// <param name="type">Type of the message</param>
        /// <param name="content">Content of the message</param>
        /// <returns>Asynchronous task</returns>
        public static void Output(MessageType type, string content) => Output(new Message(type, content));

        /// <summary>
        /// Output a generic message asynchronously
        /// </summary>
        /// <param name="type">Type of the message</param>
        /// <param name="content">Content of the message</param>
        /// <returns>Asynchronous task</returns>
        public static Task OutputAsync(MessageType type, string content) => OutputAsync(new Message(type, content));

        /// <summary>
        /// Report the diagnostics of this class
        /// </summary>
        /// <param name="builder">Target to write to</param>
        public static void Diagnostics(StringBuilder builder)
        {
            lock (_deserializationErrors)
            {
                if (_deserializationErrors.Count > 0)
                {
                    builder.AppendLine("Failed to deserialize the following properties:");
                }

                foreach (var kv in _deserializationErrors)
                {
                    builder.AppendLine($"- {kv.Value.Item1.Name} -> {kv.Key.Name} from {kv.Value.Item2.GetRawText()}");
                }
            }
        }
    }
}