using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Utility;
using DuetSharedLibrary;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;

namespace DuetControlServer.Model;

/// <summary>
/// Main object model with extensions for synchronization
/// </summary>
[DiagnosticsPriority(-3)]
public partial class ObjectModel : DuetAPI.ObjectModel.ObjectModel, IDiagnostics
{
    /// <summary>
    /// Indicates whether multiple motion systems are configured.
    /// When false, the channel Active check in FlushAsync is skipped for performance
    /// </summary>
    public volatile bool MultipleMotionSystemsConfigured;

    /// <summary>
    /// Machine mode as of the last object model update.
    /// Mirrored here so the code parser does not have to take a read lock per code
    /// </summary>
    public volatile MachineMode CurrentMachineMode;

    /// <summary>
    /// Whether Marlin is being emulated, per input channel, as of the last object model update
    /// </summary>
    private readonly bool[] _emulatingMarlin = new bool[Enum.GetValues<CodeChannel>().Length];

    /// <summary>
    /// Lock for read/write access
    /// </summary>
    private readonly AsyncReaderWriterLock _readWriteLock = new();

    /// <summary>
    /// Base lock for update conditions. This stays a Nito lock because AsyncConditionVariable binds to it
    /// </summary>
    private readonly Nito.AsyncEx.AsyncLock _updateLock = new();

    /// <summary>
    /// Completion source that is pulsed whenever the machine model has been updated. Waiters race it against a
    /// timeout instead of cancelling a condition variable, so poll timeouts do not throw
    /// </summary>
    private TaskCompletionSource _updateTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Condition variable to trigger when the machine model has been fully updated from RepRapFirmware
    /// </summary>
    private readonly AsyncConditionVariable _fullUpdateEvent;

    // Private fields
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ObjectModel> _logger;
    private readonly IOptions<Settings> _settings;

    /// <summary>
    /// Main constructor
    /// </summary>
    /// <param name="lifetime">Host application lifetime</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settings">Settings</param>
    public ObjectModel(IHostApplicationLifetime lifetime, ILogger<ObjectModel> logger, IOptions<Settings> settings)
    {
        _fullUpdateEvent = new(_updateLock);

        _lifetime = lifetime;
        _logger = logger;
        _settings = settings;

        OnDeserializationFailed += DeserializationFailedHandler;

        BuildDateTimeAttribute buildAttribute = (BuildDateTimeAttribute)Attribute.GetCustomAttribute(System.Reflection.Assembly.GetExecutingAssembly(), typeof(BuildDateTimeAttribute))!;
        SBC = new()
        {
            AppArmor = Directory.Exists("/sys/module/apparmor"),
            Distribution = GetDistribution(),
            DistributionBuildTime = GetDistributionBuildTime()
        };
        SBC.CPU.Hardware = GetCpuHardware();
        SBC.CPU.NumCores = GetCpuNumCores();
        SBC.DSF.BuildDateTime = buildAttribute.Date ?? "unknown build time";
        SBC.DSF.CommunicationMethod = settings.Value.CommunicationMethod;
        SBC.DSF.Is64Bit = Environment.Is64BitProcess;
        SBC.DSF.Version = VersionHelper.GetVersion();
        SBC.DSF.PluginSupport = settings.Value.PluginSupport;
        SBC.DSF.RootPluginSupport = settings.Value.PluginSupport && settings.Value.RootPluginSupport;
        SBC.Memory.Total = GetTotalMemory();
        SBC.Model = GetSbcModel();
        SBC.Serial = GetSbcSerial();
        Network.Hostname = Environment.MachineName;
        Network.Name = Environment.MachineName;
    }

    /// <summary>
    /// Function that is called when the object model has been updated
    /// </summary>
    private void OnModelUpdated()
    {
        // Refresh the lock-free mirrors while the write lock is still held
        CurrentMachineMode = State.MachineMode;
        MultipleMotionSystemsConfigured = Move.MotionSystems.Count > 1;
        for (int i = 0; i < Inputs.Count; i++)
        {
            InputChannel? input = Inputs[i];
            if (input is not null)
            {
                Volatile.Write(ref _emulatingMarlin[(int)input.Name], input.Compatibility is Compatibility.Marlin or Compatibility.NanoDLP);
            }
        }

        Interlocked.Exchange(ref _updateTcs, new(TaskCreationOptions.RunContinuationsAsynchronously)).SetResult();
    }

    /// <summary>
    /// Current sequence numbers for each object model section as reported by the firmware.
    /// Keys are section names (e.g. "heat", "move"), values are sequence counters.
    /// Internal so it doesn't leak into filtered object model queries via reflection
    /// </summary>
    internal Dictionary<string, int> Seqs { get; } = [];

    /// <summary>
    /// Configured password (see M551)
    /// </summary>
    internal string Password { get; set; } = DuetAPI.Connection.Defaults.Password;

    /// <summary>
    /// Whether the current machine status is overridden because an update is in progress
    /// </summary>
    internal bool IsUpdating
    {
        get => _isUpdating;
        set
        {
            if (value)
            {
                State.Status = MachineStatus.Updating;
            }
            _isUpdating = value;
        }
    }
    private bool _isUpdating;

    /// <summary>
    /// Dictionary of the properties vs. sender type + JSON content that failed to be deserialized
    /// </summary>
    private readonly Dictionary<Type, Tuple<Type, JsonElement>> _deserializationErrors = [];

    /// <summary>
    /// Event handler to be called when the deserialization of a property failed
    /// </summary>
    /// <param name="sender">Object that failed to deserialze a property</param>
    /// <param name="e">Event args pointing to the property that failed to be deserialized</param>
    private void DeserializationFailedHandler(object sender, DeserializationFailedEventArgs e)
    {
        // This may be called concurrently from any thread deserializing model data, so the check
        // must happen inside the lock to avoid racing duplicate additions
        lock (_deserializationErrors)
        {
            if (!_deserializationErrors.TryAdd(e.TargetType, new(sender.GetType(), e.JsonValue)))
            {
                return;
            }
        }
        _logger.LogError("Failed to deserialize {TypeName} -> {TargetType} from {JSON}", sender.GetType().Name, e.TargetType.Name, e.JsonValue.GetRawText());
    }

    [GeneratedRegex(@"^Hardware\s*:\s*(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex _hardwareRegex();

    /// <summary>
    /// Get the CPU hardware
    /// </summary>
    /// <returns>CPU hardware or null if unknown</returns>
    public string? GetCpuHardware()
    {
        try
        {
            Regex hardwareRegex = _hardwareRegex();
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
            _logger.LogWarning(e, "Failed to get CPU hardware");
        }
        return null;
    }

    [GeneratedRegex(@"^cpu\d", RegexOptions.IgnoreCase)]
    private static partial Regex _cpuRegex();

    /// <summary>
    /// Get the number of processor cores/threads
    /// </summary>
    /// <returns>Number of cores/threads or 1 if unknown</returns>
    public int GetCpuNumCores()
    {
        try
        {
            Regex cpuIndexRegex = _cpuRegex();
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
            _logger.LogWarning(e, "Failed to get number of CPU cores");
        }
        return 1;
    }

    /// <summary>
    /// Get the current Linux distribution
    /// </summary>
    /// <returns>Distribution name or null if unknown</returns>
    public string? GetDistribution()
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
                _logger.LogWarning(e, "Failed to get distribution");
            }
        }
        return null;
    }

    [GeneratedRegex(@"^Model\s*:\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex _modelRegex();

    /// <summary>
    /// Get the SBC model name
    /// </summary>
    /// <returns>SBC model or null if unknown</returns>
    public string? GetSbcModel()
    {
        try
        {
            Regex modelRegex = _modelRegex();
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
            _logger.LogWarning(e, "Failed to get SBC model");
        }
        return null;
    }

    [GeneratedRegex(@"^Serial\s*:\s*(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex _serialRegex();

    /// <summary>
    /// Get the SBC serial
    /// </summary>
    /// <returns>SBC model or null if unknown</returns>
    public string? GetSbcSerial()
    {
        try
        {
            Regex modelRegex = _serialRegex();
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
            _logger.LogWarning(e, "Failed to get SBC serial");
        }
        return null;
    }

    /// <summary>
    /// Determine when the current Linux distribution was built
    /// </summary>
    /// <returns>Build datetime or null if unknown</returns>
    public DateTime? GetDistributionBuildTime()
    {
        if (File.Exists("/etc/os-release"))
        {
            try
            {
                return File.GetCreationTime("/etc/os-release");
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to get distribution build time");
            }
        }
        return null;
    }

    [GeneratedRegex(@"^MemTotal:\s*(\d+)\s*(kB|KiB)", RegexOptions.IgnoreCase)]
    private static partial Regex _memTotalRegex();

    /// <summary>
    /// Get the total memory of this SBC
    /// </summary>
    /// <returns></returns>
    public long? GetTotalMemory()
    {
        if (File.Exists("/proc/meminfo"))
        {
            try
            {
                Regex totalMemoryRegex = _memTotalRegex();
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
                _logger.LogWarning(e, "Failed to get distribution build time");
            }
        }
        return null;
    }

    /// <summary>
    /// Access the machine model for read operations only
    /// </summary>
    /// <returns>Disposable lock object to be used with a using directive</returns>
    public LockWrapper AccessReadOnly(CancellationToken cancellationToken)
    {
        return new LockWrapper(_readWriteLock.ReaderLock(cancellationToken), false, OnModelUpdated, _lifetime, this, _logger, _settings);
    }

    /// <summary>
    /// Access the machine model for read operations only
    /// </summary>
    /// <returns>Disposable lock object to be used with a using directive</returns>
    public LockWrapper AccessReadOnly() => AccessReadOnly(_lifetime.ApplicationStopping);

    /// <summary>
    /// Access the machine model for read/write operations
    /// </summary>
    /// <returns>Disposable lock object to be used with a using directive</returns>
    public LockWrapper AccessReadWrite(CancellationToken cancellationToken)
    {
        return new LockWrapper(_readWriteLock.WriterLock(cancellationToken), true, OnModelUpdated, _lifetime, this, _logger, _settings);
    }

    /// <summary>
    /// Access the machine model for read/write operations
    /// </summary>
    /// <returns>Disposable lock object to be used with a using directive</returns>
    public LockWrapper AccessReadWrite() => AccessReadWrite(_lifetime.ApplicationStopping);

    /// <summary>
    /// Access the machine model asynchronously for read operations only
    /// </summary>
    /// <returns>Disposable lock object to be used with a using directive</returns>
    public async ValueTask<LockWrapper> AccessReadOnlyAsync(CancellationToken cancellationToken)
    {
        return new LockWrapper(await _readWriteLock.ReaderLockAsync(cancellationToken), false, OnModelUpdated, _lifetime, this, _logger, _settings);
    }

    /// <summary>
    /// Access the machine model asynchronously for read operations only
    /// </summary>
    /// <returns>Disposable lock object to be used with a using directive</returns>
    public ValueTask<LockWrapper> AccessReadOnlyAsync() => AccessReadOnlyAsync(_lifetime.ApplicationStopping);

    /// <summary>
    /// Access the machine model asynchronously for read/write operations
    /// </summary>
    /// <returns>Disposable lock object to be used with a using directive</returns>
    public async ValueTask<LockWrapper> AccessReadWriteAsync(CancellationToken cancellationToken)
    {
        return new LockWrapper(await _readWriteLock.WriterLockAsync(cancellationToken), true, OnModelUpdated, _lifetime, this, _logger, _settings);
    }

    /// <summary>
    /// Access the machine model asynchronously for read/write operations
    /// </summary>
    /// <returns>Disposable lock object to be used with a using directive</returns>
    public ValueTask<LockWrapper> AccessReadWriteAsync() => AccessReadWriteAsync(_lifetime.ApplicationStopping);

    /// <summary>
    /// Check if Marlin is being emulated on the given channel
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <returns>True if Marlin is being emulated</returns>
    public bool IsEmulatingMarlin(CodeChannel channel) => Volatile.Read(ref _emulatingMarlin[(int)channel]);

    /// <summary>
    /// Wait for an update to occur
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    public void WaitForUpdate(CancellationToken cancellationToken) => WaitForUpdateAsync(cancellationToken).GetAwaiter().GetResult();

    /// <summary>
    /// Wait for an update to occur asynchronously
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public Task WaitForUpdateAsync(CancellationToken cancellationToken) => Volatile.Read(ref _updateTcs).Task.WaitAsync(cancellationToken);

    /// <summary>
    /// Wait for an update to occur asynchronously, giving up after the given timeout
    /// </summary>
    /// <param name="timeout">Maximum time to wait in ms</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if an update occurred, false on timeout</returns>
    public async Task<bool> WaitForUpdateAsync(int timeout, CancellationToken cancellationToken)
    {
        Task updateTask = Volatile.Read(ref _updateTcs).Task;
        if (await Task.WhenAny(updateTask, Task.Delay(timeout, cancellationToken)) != updateTask)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        return true;
    }

    /// <summary>
    /// Wait for an update to occur
    /// </summary>
    public void WaitForUpdate() => WaitForUpdate(_lifetime.ApplicationStopping);

    /// <summary>
    /// Wait for an update to occur asynchronously
    /// </summary>
    /// <returns>Asynchronous task</returns>
    public Task WaitForUpdateAsync() => WaitForUpdateAsync(_lifetime.ApplicationStopping);

    /// <summary>
    /// Wait for the model to be fully updated from RepRapFirmware
    /// </summary>
    public void WaitForFullUpdate()
    {
        using (_updateLock.Lock())
        {
            _fullUpdateEvent.Wait();
        }
    }

    /// <summary>
    /// Wait asynchronously for the model to be fully updated from RepRapFirmware
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task WaitForFullUpdateAsync(CancellationToken cancellationToken = default)
    {
        using (await _updateLock.LockAsync(cancellationToken))
        {
            await _fullUpdateEvent.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Called in non-SPI mode to notify waiting tasks about a finished model update (synchronous version)
    /// </summary>
    internal void FullyUpdated()
    {
        using (_updateLock.Lock())
        {
            _fullUpdateEvent.NotifyAll();
        }
    }

    /// <summary>
    /// Called in non-SPI mode to notify waiting tasks about a finished model update
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    internal async Task FullyUpdatedAsync(CancellationToken cancellationToken = default)
    {
        using (await _updateLock.LockAsync(cancellationToken))
        {
            _fullUpdateEvent.NotifyAll();
        }
    }

    /// <summary>
    /// Indicates how many config files are being processed
    /// </summary>
    private int _numRunningConfigFiles = 0;

    /// <summary>
    /// Flag asynchronously that a start-up file is being executed. Must be called WITHOUT locking this instance first!
    /// </summary>
    /// <param name="executing">Whether a start-up file is being executed or not</param>
    public void SetExecutingConfig(bool executing)
    {
        if (executing)
        {
            Interlocked.Increment(ref _numRunningConfigFiles);
        }
        else
        {
            Interlocked.Decrement(ref _numRunningConfigFiles);
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
    public async Task HandleMacroErrorAsync(string fileName, long lineNumber, string message)
    {
        string shortFileName = Path.GetFileName(fileName);
        using (await AccessReadWriteAsync())
        {
            if (_numRunningConfigFiles > 0 && State.StartupError == null)
            {
                State.StartupError = new()
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
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the message has been written</returns>
    public bool Output(EventLogLevel level, Message message, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(message?.Content))
        {
            using (AccessReadWrite(cancellationToken))
            {
                // Can we output this message?
                if (State.LogLevel == EventLogLevel.Off || (byte)State.LogLevel + (byte)level < 3)
                {
                    return false;
                }

                // Print the message to the DCS log
                switch (message.Type)
                {
                    case MessageType.Error:
                        _logger.LogError("{Message}", message.Content);
                        break;
                    case MessageType.Warning:
                        _logger.LogWarning("{Message}", message.Content);
                        break;
                    default:
                        _logger.LogInformation("{Message}", message.Content);
                        break;
                }

                // Send it to the object model
                Messages.Add(message);
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
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Whether the message has been written</returns>
    public async Task<bool> OutputAsync(EventLogLevel level, Message message, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(message?.Content))
        {
            using (await AccessReadWriteAsync(cancellationToken))
            {
                // Can we output this message?
                if (State.LogLevel == EventLogLevel.Off || (byte)State.LogLevel + (byte)level < 3)
                {
                    return false;
                }

                // Print the message to the DCS log
                switch (message.Type)
                {
                    case MessageType.Error:
                        _logger.LogError("{Message}", message.Content);
                        break;
                    case MessageType.Warning:
                        _logger.LogWarning("{Message}", message.Content);
                        break;
                    default:
                        _logger.LogInformation("{Message}", message.Content);
                        break;
                }

                // Send it to the object model
                Messages.Add(message);
            }

            return true;
        }
        return false;
    }

    /// <summary>
    /// Output a generic message
    /// </summary>
    /// <param name="message">Message to output</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public void Output(Message message, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(message?.Content))
        {
            // Print the message to the DCS log
            switch (message.Type)
            {
                case MessageType.Error:
                    _logger.LogError("{Message}", message.Content);
                    break;
                case MessageType.Warning:
                    _logger.LogWarning("{Message}", message.Content);
                    break;
                default:
                    _logger.LogInformation("{Message}", message.Content);
                    break;
            }

            // Send it to the object model
            using (AccessReadWrite(cancellationToken))
            {
                Messages.Add(message);
            }
        }
    }

    /// <summary>
    /// Output a generic message asynchronously
    /// </summary>
    /// <param name="message">Message to output</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task OutputAsync(Message message, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(message?.Content))
        {
            // Print the message to the DCS log
            switch (message.Type)
            {
                case MessageType.Error:
                    _logger.LogError("{Message}", message.Content);
                    break;
                case MessageType.Warning:
                    _logger.LogWarning("{Message}", message.Content);
                    break;
                default:
                    _logger.LogInformation("{Message}", message.Content);
                    break;
            }

            // Send it to the object model
            using (await AccessReadWriteAsync(cancellationToken))
            {
                Messages.Add(message);
            }
        }
    }

    /// <summary>
    /// Output a generic message
    /// </summary>
    /// <param name="type">Type of the message</param>
    /// <param name="content">Content of the message</param>
    /// <returns>Asynchronous task</returns>
    public void Output(MessageType type, string content) => Output(new Message(type, content));

    /// <summary>
    /// Output a generic message asynchronously
    /// </summary>
    /// <param name="type">Type of the message</param>
    /// <param name="content">Content of the message</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public Task OutputAsync(MessageType type, string content, CancellationToken cancellationToken) => OutputAsync(new Message(type, content), cancellationToken);

    /// <summary>
    /// Report the diagnostics of this class
    /// </summary>
    /// <param name="builder">Target to write to</param>
    public void PrintDiagnostics(StringBuilder builder)
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

    /// <summary>
    /// Event that is raised when the connection to the firmware has been lost
    /// </summary>
    public event EventHandler? OnConnectionLost;

    /// <summary>
    /// Called by the link subsystem when the connection to the Duet has been lost
    /// </summary>
    internal void ConnectionLost()
    {
        using (AccessReadWrite())
        {
            Boards.Clear();
            Global.Clear();
            Seqs.Clear();
            if (State.Status != MachineStatus.Halted && State.Status != MachineStatus.Updating)
            {
                State.Status = MachineStatus.Disconnected;
            }
            State.DisplayMessage = string.Empty;
            State.MessageBox = null;
        }

        OnConnectionLost?.Invoke(this, EventArgs.Empty);
    }
}
