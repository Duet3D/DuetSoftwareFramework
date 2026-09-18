using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Heat;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Motion.Native;
using DuetControlServer.Tools;
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
    /// When false, the channel Active check in FlushAsync is skipped for performance.
    /// Updated by the model update service when the "move" key is refreshed.
    /// </summary>
    public volatile bool MultipleMotionSystemsConfigured;

    /// <summary>
    /// Lock for read/write access
    /// </summary>
    private readonly AsyncReaderWriterLock _readWriteLock = new();

    /// <summary>
    /// Completion source that is pulsed whenever the machine model has been updated. Waiters race it against a
    /// timeout instead of cancelling a condition variable, so poll timeouts do not throw
    /// </summary>
    private TaskCompletionSource _updateTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        SBC.DSF.Is64Bit = Environment.Is64BitProcess;
        SBC.DSF.Version = VersionHelper.GetVersion();
        SBC.DSF.PluginSupport = settings.Value.PluginSupport;
        SBC.DSF.RootPluginSupport = settings.Value.PluginSupport && settings.Value.RootPluginSupport;
        SBC.Memory.Total = GetTotalMemory();
        SBC.Model = GetSbcModel();
        SBC.Serial = GetSbcSerial();
        Network.Hostname = Environment.MachineName;
        Network.Name = Environment.MachineName;
        SetLimits();
        AddMainBoard();
    }

    /// <summary>
    /// Put the main board in <c>boards[0]</c>, where every client and every code that reaches for
    /// "the main board" already looks for it
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>boards[]</c> is the main board followed by the expansion boards it knows,
    /// in ascending CAN address order (RepRap.cpp objectModelArrayTable entry 0 over
    /// ExpansionManager::GetBoardDetails). The main board here is this program and DuetCANMaster at
    /// CAN address 0, which is always there, so the entry is made once rather than discovered
    /// </remarks>
    private void AddMainBoard()
        => Boards.Add(new Board { CanAddress = CanId.MasterAddress, State = BoardState.Running });

    /// <summary>
    /// The firmware version the machine reports, or null while no board has said what it runs
    /// </summary>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's main board answers this for itself, and everything that wants "the version
    /// this machine runs" reads <c>boards[0]</c>. The main board here is this program and
    /// DuetCANMaster, and nothing tells DuetControlServer what firmware DuetCANMaster is running, so
    /// <c>boards[0]</c> has no version to give. The expansion boards do report theirs when they
    /// announce themselves, and a machine's boards run the same release, so the first one to have
    /// said is the answer.
    /// </para>
    /// <para>
    /// Null rather than an empty string, because "no board has reported yet" and "a board reported
    /// nothing" are the same thing to a caller and neither is a version to compare against
    /// </para>
    /// </remarks>
    public string? FirmwareVersion
        => Boards.FirstOrDefault(board => !string.IsNullOrEmpty(board.FirmwareVersion))?.FirmwareVersion;

    /// <summary>
    /// The object model entry for the board at a CAN address, creating it if the machine has not
    /// seen that board yet
    /// </summary>
    /// <param name="address">CAN address of the board</param>
    /// <returns>Its entry in <c>boards[]</c></returns>
    /// <remarks>
    /// <para>
    /// Kept in ascending CAN address order behind the main board, which is what makes an index into
    /// <c>boards[]</c> mean something: a client reading <c>boards[2]</c> is reading the same board on
    /// every run, and on a machine with no gaps in its addresses it is the board at address 2 - which
    /// is what a reference recorded against RepRapFirmware compares against.
    /// </para>
    /// <para>
    /// Discovery order would give neither. The order the boards happened to announce themselves in
    /// says nothing about the machine, and a board created early because config.g recorded a setting
    /// for it would sort ahead of one that announced itself first
    /// </para>
    /// </remarks>
    public Board GetOrCreateBoard(byte address)
    {
        for (int i = 0; i < Boards.Count; i++)
        {
            if (Boards[i].CanAddress == address)
            {
                return Boards[i];
            }
            if (Boards[i].CanAddress > address)
            {
                Board inserted = new() { CanAddress = address, State = BoardState.Unknown };
                Boards.Insert(i, inserted);
                return inserted;
            }
        }

        Board board = new() { CanAddress = address, State = BoardState.Unknown };
        Boards.Add(board);
        return board;
    }

    /// <summary>
    /// The board at a CAN address, or null if the machine has not seen it
    /// </summary>
    /// <param name="address">CAN address of the board</param>
    /// <returns>Its entry in <c>boards[]</c>, or null</returns>
    /// <remarks>
    /// The address is a field to match on rather than an index, because a machine may have gaps in
    /// its addresses and <c>boards[2]</c> then means the second board rather than the board at 2
    /// </remarks>
    public Board? FindBoard(byte address)
    {
        foreach (Board board in Boards)
        {
            if (board.CanAddress == address)
            {
                return board;
            }
        }
        return null;
    }

    /// <summary>
    /// Publish the limits the machine is built to, so a client sizing itself against them and the
    /// codes enforcing them read the same numbers
    /// </summary>
    /// <remarks>
    /// <para>
    /// The values are constants rather than anything discovered at run time, which is why they are
    /// set here and never again. Each comes from whatever owns it: a limit shared with the expansion
    /// boards from the CAN message schema, because a bitmap on the bus is what bounds it and both
    /// sides have to agree on the width; the rest from the subsystem that enforces them, or from
    /// <see cref="MachineLimits"/> where nothing enforces one yet.
    /// </para>
    /// <para>
    /// <c>drivers</c> and <c>volumes</c> stay null, which the object model reads as unknown rather
    /// than as unlimited. Neither has a figure this side can state: every driver is on an expansion
    /// board, so what bounds the total is how many boards are attached, and the volumes are whatever
    /// the SBC has mounted when it is asked. RepRapFirmware takes both from main board hardware that
    /// this architecture does not have
    /// </para>
    /// </remarks>
    private void SetLimits()
    {
        Limits.Axes = MotionLimits.MaxAxes;
        Limits.AxesPlusExtruders = MotionLimits.MaxAxesPlusExtruders;
        Limits.BedHeaters = HeatManager.MaxBedHeaters;
        Limits.Boards = CanId.MaxCanAddress + 1;
        Limits.ChamberHeaters = HeatManager.MaxChamberHeaters;
        Limits.DriversPerAxis = MotionLimits.MaxDriversPerAxis;
        Limits.Extruders = MotionLimits.MaxExtruders;
        Limits.ExtrudersPerTool = ToolManager.MaxExtrudersPerTool;
        Limits.Fans = CanLimits.MaxFans;
        Limits.GpInPorts = CanLimits.MaxGpInPorts;
        Limits.GpOutPorts = CanLimits.MaxGpOutPorts;
        Limits.Heaters = CanLimits.MaxHeaters;
        Limits.HeatersPerTool = HeatManager.MaxHeatersPerTool;
        Limits.LedStrips = CanLimits.MaxLedStrips;
        Limits.MonitorsPerHeater = CanLimits.MaxMonitorsPerHeater;
        Limits.PortsPerHeater = HeatManager.MaxPortsPerHeater;
        Limits.ReportedAxes = MachineLimits.MaxReportedAxes;
        Limits.RestorePoints = Motion.RestorePoint.NumVisible;
        Limits.Sensors = CanLimits.MaxSensors;
        Limits.Spindles = CanLimits.MaxSpindles;
        Limits.Tools = ToolManager.MaxTools;
        Limits.TrackedObjects = MachineLimits.MaxTrackedObjects;
        Limits.Triggers = MachineLimits.MaxTriggers;
        Limits.Workplaces = MachineLimits.NumWorkplaces;
        Limits.ZProbeProgramBytes = CanLimits.MaxZProbeProgramBytes;
        Limits.ZProbes = CanLimits.MaxZProbes;
    }

    /// <summary>
    /// Function that is called when the object model has been updated
    /// </summary>
    private void OnModelUpdated() => Interlocked.Exchange(ref _updateTcs, new(TaskCreationOptions.RunContinuationsAsynchronously)).SetResult();

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
    /// Whether a firmware update is in progress
    /// </summary>
    /// <remarks>
    /// One of the conditions <c>MachineStatusService</c> derives <c>state.status</c> from. Setting it
    /// no longer writes the status itself: RepRapFirmware computes its status from conditions like
    /// this one rather than storing it, and a condition that also wrote the answer would be one of
    /// several writers racing to describe the same machine
    /// </remarks>
    internal bool IsUpdating { get; set; }

    /// <summary>
    /// Whether an emergency stop has halted the machine (M112)
    /// </summary>
    /// <remarks>Cleared by a reset, which is the only thing that ends a halt</remarks>
    internal bool IsHalted { get; set; }

    /// <summary>
    /// Whether the link to the machine is down
    /// </summary>
    internal bool IsDisconnected { get; set; }

    /// <summary>
    /// Whether the machine is still starting up, which it is until config.g has run
    /// </summary>
    internal bool IsStarting { get; set; } = true;

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
    /// Check asynchronously if Marlin is being emulated on the given channel
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if Marlin is being emulated</returns>
    public async ValueTask<bool> IsEmulatingMarlinAsync(CodeChannel channel, CancellationToken cancellationToken = default)
    {
        using (await AccessReadOnlyAsync(cancellationToken))
        {
            Compatibility compatibility = Inputs[channel]?.Compatibility ?? Compatibility.RepRapFirmware;
            return compatibility is Compatibility.Marlin or Compatibility.NanoDLP;
        }
    }

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
    /// Indicates how many config files are being processed
    /// </summary>
    private int _numRunningConfigFiles = 0;

    /// <summary>
    /// Whether config.g or a file it calls is running
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>runningConfigFile</c>. Its <c>CheckFinishedRunningConfigFile</c> uses it
    /// to let modal state set in config.g and the files it calls persist rather than being restored
    /// when each frame ends, which is what makes an <c>M83</c> in config.g stick
    /// </remarks>
    public bool IsExecutingConfig => Volatile.Read(ref _numRunningConfigFiles) > 0;

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
            AddMainBoard();
            Global.Clear();
            Seqs.Clear();
            IsDisconnected = true;
            State.DisplayMessage = string.Empty;
            State.MessageBox = null;
        }

        OnConnectionLost?.Invoke(this, EventArgs.Empty);
    }
}
