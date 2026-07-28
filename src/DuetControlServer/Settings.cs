using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetControlServer.Utility;
using DuetSharedLibrary;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

// The regular expressions in this class are loaded from a JSON file, thus must be flexible anyway
#pragma warning disable SYSLIB1045

namespace DuetControlServer;

/// <summary>
/// Settings class
/// </summary>
public sealed class Settings
{
    /// <summary>
    /// Default path to the configuration file
    /// </summary>
    public const string DefaultConfigFile = "/opt/dsf/conf/config.json";

    /// <summary>
    /// Default path to the list of enabled plugins
    /// </summary>
    private const string DefaultPluginsFile = "/opt/dsf/conf/plugins.txt";

    /// <summary>
    /// Path to the configuration file
    /// </summary>
    [JsonIgnore]
    public string ConfigFile = DefaultConfigFile;

    /// <summary>
    /// Indicates if this program is only launched to update the board firmware
    /// </summary>
    [JsonIgnore]
    public bool UpdateOnly { get; set; }

    /// <summary>
    /// Default regular expression flags used for parsing G-code files
    /// </summary>
    private const RegexOptions DefaultRegexFlags = RegexOptions.IgnoreCase | RegexOptions.Singleline;

    /// <summary>
    /// Defines whether the mainboard and expansion boards may be updated automatically during unattended upgrades
    /// </summary>
    public bool AutoUpdateFirmware { get; set; } = true;

    /// <summary>
    /// Whether this DCS instance may support third-party plugins.
    /// If this is set to false, dsf-config.g will be run right after the start
    /// </summary>
    public bool PluginSupport { get; set; } = true;

    /// <summary>
    /// Whether this DCS instance may support third-party root plugins.
    /// This is only respected if <see cref="PluginSupport"/> is set to true
    /// </summary>
    public bool RootPluginSupport { get; set; }

    /// <summary>
    /// Disable installation of third-party plugins using the IPC API
    /// </summary>
    public bool DisablePluginInstallations { get; set; }

    /// <summary>
    /// Path to the file holding a list of loaded plugins
    /// </summary>
    public string PluginsFilename { get; set; } = DefaultPluginsFile;

    /// <summary>
    /// Time to wait before auto-restarting a stopped plugin that has the SbcAutoRestart option set
    /// </summary>
    public int PluginAutoRestartInterval { get; set; } = 2000;

    /// <summary>
    /// Minimum log level for console output
    /// </summary>
    [JsonConverter(typeof(LogLevelJsonConverter))]
    public LogLevel LogLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// Directory in which DSF-related UNIX sockets reside
    /// </summary>
    public string SocketDirectory { get; set; } = Defaults.SocketDirectory;

    /// <summary>
    /// UNIX socket file for DuetControlServer
    /// </summary>
    /// <seealso cref="DuetAPI"/>
    public string SocketFile { get; set; } = Defaults.SocketFile;

    /// <summary>
    /// Fully-qualified path to the main IPC UNIX socket (evaluated during runtime)
    /// </summary>
    [JsonIgnore]
    public string FullSocketPath => Path.Combine(SocketDirectory, SocketFile);

    /// <summary>
    /// File to contain the last start error of DCS. Once DCS starts successfully, it is deleted
    /// </summary>
    public string StartErrorFile { get; set; } = Defaults.StartErrorFile;

    /// <summary>
    /// Maximum number of simultaneously pending IPC connections
    /// </summary>
    public int Backlog { get; set; } = 4;

    /// <summary>
    /// Virtual SD card directory.
    /// Paths starting with 0:/ are mapped to this directory
    /// </summary>
    public string BaseDirectory { get; set; } = "/opt/dsf/sd";

    /// <summary>
    /// Directory holding DSF plugins
    /// </summary>
    /// <remarks>
    /// This directory is not created by the DCS package. It is provided by DPS
    /// </remarks>
    public string PluginDirectory { get; set; } = "/opt/dsf/plugins";

    /// <summary>
    /// Internal model update interval after which properties of the machine model from
    /// the host controller (e.g. network information and mass storage devices) are updated (in ms)
    /// </summary>
    public int HostUpdateInterval { get; set; } = 4000;

    /// <summary>
    /// Maximum time to keep messages in the object model unless client(s) pick them up (in s).
    /// Note that messages are only cleared when the host update task runs.
    /// </summary>
    public double MaxMessageAge { get; set; } = 60.0;

    /// <summary>
    /// SPI device that is connected to RepRapFirmware
    /// </summary>
    public string SpiDevice { get; set; } = "/dev/spidev0.0";

    /// <summary>
    /// Tx and Rx buffer size for SBC protocol transfers.
    /// Only respected in SPI mode and must not exceed the kernel spidev buffer size
    /// </summary>
    public int SbcBufferSize { get; set; } = Link.Protocol.Shared.Consts.BufferSize;

    /// <summary>
    /// SPI Transfer Mode 0-3
    /// </summary>
    public int SpiTransferMode { get; set; } = 0;

    /// <summary>
    /// Frequency to use for SPI transfers (in Hz)
    /// </summary>
    public int SpiFrequency { get; set; } = 8_000_000;

    /// <summary>
    /// Whether to isolate the SPI thread on a dedicated CPU core (only relevant on Raspberry Pi)
    /// </summary>
    public bool IsolateInterfaceThread { get; set; } = true;

    /// <summary>
    /// Whether to isolate the motion thread on a dedicated CPU core (only relevant on Raspberry Pi)
    /// </summary>
    public bool IsolateMotionThread { get; set; } = true;

    /// <summary>
    /// The CPU core which has been isolated from the OS scheduler. The SPI interface thread and the GPIO
    /// monitor threads are pinned here; the motion thread is pinned here too unless <see cref="MotionCoreId"/>
    /// is set. For best latency this should be a core reserved via the kernel <c>isolcpus</c> boot parameter
    /// </summary>
    public int IsolatedCoreId { get; set; } = 3;

    /// <summary>
    /// CPU core to pin the motion thread to. When negative, the motion thread shares
    /// <see cref="IsolatedCoreId"/> with the SPI interface thread. Placing motion on its own isolated core
    /// avoids the two real-time threads competing for the same CPU
    /// </summary>
    public int MotionCoreId { get; set; } = -1;

    /// <summary>
    /// CPU core to pin the GPIO edge-monitor threads (TfrRdy/DataAvailable) to. When negative, they share
    /// <see cref="IsolatedCoreId"/> with the SPI interface thread so that waking the interface thread is a
    /// cheap local context switch rather than a cross-core wake-up
    /// </summary>
    public int GpioMonitorCoreId { get; set; } = -1;

    /// <summary>
    /// Whether to run the interface, motion and GPIO monitor threads under the SCHED_FIFO real-time
    /// scheduling policy (only relevant on Raspberry Pi and requires CAP_SYS_NICE). This is what actually
    /// bounds scheduling jitter on a PREEMPT_RT kernel; without it these threads run under CFS and can be
    /// preempted for tens of milliseconds
    /// </summary>
    public bool UseRealtimeScheduling { get; set; } = true;

    /// <summary>
    /// SCHED_FIFO priority for the GPIO edge-monitor threads. This must be the highest of the three because
    /// the interface thread cannot make progress until a monitor thread has delivered the pin edge that
    /// unblocks it
    /// </summary>
    public int GpioMonitorRtPriority { get; set; } = 60;

    /// <summary>
    /// SCHED_FIFO priority for the SPI interface thread. Should sit below <see cref="GpioMonitorRtPriority"/>
    /// and above <see cref="MotionRtPriority"/>
    /// </summary>
    public int InterfaceRtPriority { get; set; } = 50;

    /// <summary>
    /// SCHED_FIFO priority for the motion thread. Should sit below <see cref="InterfaceRtPriority"/> so that
    /// the SPI transfer is never starved by motion computation when they share a core
    /// </summary>
    public int MotionRtPriority { get; set; } = 40;

    /// <summary>
    /// Maximum allowed time when waiting for the first transfer (in ms)
    /// </summary>
    public int SbcConnectTimeout { get; set; } = 500;

    /// <summary>
    /// Maximum allowed delay between data exchanges during a full transfer (in ms)
    /// </summary>
    public int SbcTransferTimeout { get; set; } = 500;

    /// <summary>
    /// Maximum allowed delay between full transfers (in ms)
    /// </summary>
    public int SbcConnectionTimeout { get; set; } = 4000;

    /// <summary>
    /// Maximum time to wait for a reason to initiate a full transfer before performing a keep-alive
    /// transfer anyway (in ms). When idle a transfer is only started once DSF has data to send or the
    /// controller raises the data available pin, but a transfer is forced at least this often so that
    /// disconnects are still detected
    /// </summary>
    public int SbcConnectionKeepAliveInterval { get; set; } = 25;

    /// <summary>
    /// Maximum number of sequential transfer retries
    /// </summary>
    public int MaxSbcRetries { get; set; } = 3;

    /// <summary>
    /// Timeout for CAN requests that expect a reply (in ms).
    /// </summary>
    public int CanRequestTimeout { get; set; } = 2000;

    /// <summary>
    /// Path to the GPIO chip device node
    /// </summary>
    public string GpioChipDevice { get; set; } = "/dev/gpiochip0";

    /// <summary>
    /// Number of the GPIO pin that is used by RepRapFirmware to flag its ready state
    /// </summary>
    public int TransferReadyPin { get; set; } = 25;      // Pin 22 on the RaspPi expansion header

    /// <summary>
    /// Number of the GPIO pin that is used by DuetCANMaster to flag that it has data to send to the DSF
    /// </summary>
    public int DataAvailablePin { get; set; } = 24;      // Pin 18 on the RaspPi expansion header

#if DEBUG
    public int SbcDataAvailablePin { get; set; } = 23;    // Pin 16 on the RaspPi expansion header
#endif
    /// <summary>
    /// USB device that is connected to RepRapFirmware (e.g., /dev/ttyACM1)
    /// </summary>
    public string UsbDevice { get; set; } = "/dev/ttyACM1";

    /// <summary>
    /// Read timeout for USB serial communication in milliseconds
    /// </summary>
    public int UsbReadTimeout { get; set; } = 2000;

    /// <summary>
    /// Write timeout for USB serial communication in milliseconds
    /// </summary>
    public int UsbWriteTimeout { get; set; } = 2000;

    /// <summary>
    /// File containing the current CPU temperature
    /// </summary>
    public string CpuTemperaturePath { get; set; } = "/sys/class/thermal/thermal_zone0/temp";

    /// <summary>
    /// Divide numeric value of <see cref="CpuTemperaturePath"/> by this
    /// </summary>
    public float CpuTemperatureDivider { get; set; } = 1000F;

    /// <summary>
    /// Number of codes to buffer in the internal print subsystem
    /// </summary>
    public int BufferedPrintCodes { get; set; } = 32;

    /// <summary>
    /// Number of codes to buffer per macro
    /// </summary>
    public int BufferedMacroCodes { get; set; } = 16;

    /// <summary>
    /// Maximum number of pending codes per code channel
    /// </summary>
    public int MaxCodesPerInput { get; set; } = 32;

    /// <summary>
    /// Maximum size of a binary encoded G/M/T-code. This is limited by RepRapFirmware (see code queue)
    /// </summary>
    public int MaxCodeBufferSize { get; set; } = 384;

    /// <summary>
    /// Maximum supported length of messages to be sent to RepRapFirmware
    /// </summary>
    public int MaxMessageLength { get; set; } = 4096;

    /// <summary>
    /// Whether to allow custom patches to the object model. Not recommended
    /// </summary>
    public bool AllowCustomModelPatches { get; set; }

    /// <summary>
    /// List of string chunks that are identified by RepRapFirmware
    /// </summary>
    /// <remarks>
    /// Only if a comment contains one of these identifiers they will be sent to the firmware
    /// </remarks>
    public List<string> FirmwareComments { get; set; } =
    [
        "printing object",			// slic3r
        "MESH",						// Cura
        "process",					// S3D
        "stop printing object",		// slic3r
        "layer",					// S3D "; layer 1, z=0.200"
        "LAYER",					// Ideamaker, Cura (followed by layer number starting at zero)
        "BEGIN_LAYER_OBJECT z=",	// KISSlicer (followed by Z height)
        "HEIGHT",					// Ideamaker
        "PRINTING",					// Ideamaker
        "REMAINING_TIME"			// Ideamaker
    ];

    /// <summary>
    /// Interval of object model updates (in ms)
    /// </summary>
    public int ModelUpdateInterval { get; set; } = 100;

    public string FirmwareFilePrefix { get; set; } = "Duet3Firmware_";

    public string BootloaderFilePrefix { get; set; } = "Duet3Bootloader_";

    /// <summary>
    /// Maximum lock time of the object model. If this time is exceeded, a deadlock is reported and the application is terminated.
    /// Set this to -1 to disable the automatic deadlock detection
    /// </summary>
    public int MaxMachineModelLockTime { get; set; } = -1;

    /// <summary>
    /// Size of the read buffer used when reading from files (in bytes)
    /// </summary>
    public int FileBufferSize { get; set; } = 32768;

    /// <summary>
    /// Initial size of the buffers used to serialize IPC JSON responses like the object model and of the
    /// chunks used to receive IPC JSON messages (in bytes). Serialization buffers grow automatically if
    /// a response exceeds this size
    /// </summary>
    public int IpcJsonBufferSize { get; set; } = 1024;

    /// <summary>
    /// How many bytes to parse max at the beginning of a file to retrieve G-code file information (in bytes)
    /// </summary>
    public int FileInfoReadLimitHeader { get; set; } = 16384;

    /// <summary>
    /// How many bytes to parse max at the end of a file to retrieve G-code file information (in bytes)
    /// </summary>
    public int FileInfoReadLimitFooter { get; set; } = 262144;

    /// <summary>
    /// Maximum allowed layer height. Used by the file info parser
    /// </summary>
    public double MaxLayerHeight { get; set; } = 0.9;

    /// <summary>
    /// Regular expressions for finding the layer height (case insensitive)
    /// </summary>
    public List<string> LayerHeightFilters { get; set; } =
    [
        @"^\s*layer_height\D+(?<mm>(\d+\.?\d*))",            // Slic3r / Prusa Slicer
        @"Layer height\D+(?<mm>(\d+\.?\d*))",                // Cura
        @"layerHeight\D+(?<mm>(\d+\.?\d*))",                 // Simplify3D
        @"layer_thickness_mm\D+(?<mm>(\d+\.?\d*))",          // KISSlicer and Canvas
        @"layerThickness\D+(?<mm>(\d+\.?\d*))",              // Matter Control
        @"sliceHeight\D+(?<mm>(\d+\.?\d*))"                  // Kiri:Moto
    ];

    /// <summary>
    /// Regular expressions for finding the total number of layers
    /// </summary>
    /// <remarks>
    /// If the number of layers cannot be found, the total number of layers is calculated from the layer and object heights (if applicable)
    /// </remarks>
    public List<string> NumLayersFilters { get; set; } =
    [
        @"NUM_LAYERS\D+(\d+)"
    ];

    /// <summary>
    /// Regular expressions for finding the filament consumption (case insensitive, single line)
    /// </summary>
    public List<string> FilamentFilters { get; set; } =
    [
        @"filament used\D+(((?<mm>\d+\.?\d*)\s*mm)(\D+)?)+",                     // Slic3r and Kiri:Moto (mm)
        @"filament used\D+(((?<m>\d+\.?\d*)m([^m]|$))(\D+)?)+",                  // Cura (m)
        @"filament length\D+(((?<mm>\d+\.?\d*)\s*mm)(\D+)?)+",                   // Simplify3D (mm)
        @"filament used \[mm\]\D+((?<mm>\d+\.?\d*)(\D+)?)+",                     // Prusa Slicer (mm)
        @"material\#(?<index>\d+)\D+(?<mm>\d+\.?\d*)",                           // IdeaMaker (mm)
        @"Ext\s*\#\d+\D+(?<mm>\d+\.?\d*)",                                       // KISSSlicer v2.0 (mm)
        @"Filament used per extruder:\r\n;\s*(?<name>.+)\s+=\s*(?<mm>[0-9.]+)",  // Canvas
        @"filament used extruder (?<index>\d+) \(mm\) = (?<mm>\d+\.?\d*)"        // MatterControl v2
    ];

    /// <summary>
    /// Regular expressions for finding the slicer (case insensitive)
    /// </summary>
    public List<string> GeneratedByFilters { get; set; } =
    [
        @"generated by\s+(.+)",                              // Slic3r, Simplify3D, Kiri:Moto
        @"Sliced by\s+(.+)",                                 // IdeaMaker and Canvas
        @"(KISSlicer.*)",                                    // KISSlicer
        @"Sliced at:\s*(.+)",                                // Cura (old)
        @"Generated with\s*(.+)"                             // Cura (new)
    ];

    /// <summary>
    /// Regular expressions for finding the print time
    /// </summary>
    public List<string> PrintTimeFilters { get; set; } =
    [
        @"estimated printing time .*= ((?<d>(\d+))d\s*)?((?<h>(\d+))h\s*)?((?<m>(\d+))m\s*)?((?<s>(\d+))s)?",                // Slic3r PE
        @"TIME:(?<s>(\d+\.?\d*))",                                                                                           // Cura
        @"Build Time:\s+((?<h>(\d+\.?\d*)) hour(s)?\s*)?((?<m>(\d+\.?\d*)) minute(s)?\s*)?((?<s>(\d+\.?\d*)) second(s)?)?",  // Simplify3D, KISSlicer, Canvas, IceSL
        @"print time:\s+(?<s>(\d+\.?\d*))(s)?",                                                                              // Kiri:Moto, and IdeaMaker v4
        @"Total estimated \(pre-cool\) minutes: ((?<m>\d+\.?\d*))",                                                          // KISSlicer v2.0
        @"total print time \(s\) = (?<s>(\d+\.?\d*))",                                                                       // MatterControl v2
        @"Build time:\s+(?<h>(\d+\.?\d*)):(?<m>(\d+\.?\d*)):(?<s>(\d+\.?\d*))"                                               // REACTOR
    ];

    /// <summary>
    /// Regular expressions for finding the simulated time
    /// </summary>
    public List<string> SimulatedTimeFilters { get; set; } =
    [
        @"Simulated print time\D+(?<s>(\d+\.?\d*))"
    ];

    /// <summary>
    /// Compile the given filter patterns
    /// </summary>
    /// <param name="patterns">Patterns to compile</param>
    /// <returns>Compiled regular expressions</returns>
    /// <remarks>
    /// The filters are stored as plain patterns because <see cref="Regex"/> cannot be bound from configuration,
    /// so options have to be given inline (for example <c>(?-i)</c> to match case-sensitively)
    /// </remarks>
    internal static List<Regex> CompileFilters(List<string> patterns) => patterns.ConvertAll(pattern => new Regex(pattern, DefaultRegexFlags));

    /// <summary>
    /// Perform final configuration steps
    /// </summary>
    public void PostConfigure()
    {
        if (!File.Exists(ConfigFile) && Directory.Exists(Path.GetDirectoryName(ConfigFile)))
        {
            // Save default settings to the config file
            SaveToFile(ConfigFile);
        }
    }

    /// <summary>
    /// Save settings to a given file
    /// </summary>
    /// <param name="fileName">File to save the settings to</param>
    private void SaveToFile(string fileName)
    {
        using FileStream fileStream = new(fileName, FileMode.Create, FileAccess.Write, FileShare.None, FileBufferSize);
        JsonSerializer.Serialize(fileStream, this, SettingsContext.Default.Settings);
    }
}

/// <summary>
/// Extensions for the service collection
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Legacy settings keys (DSF 3.6 and earlier) mapped to their current equivalents.
    /// The SPI buffer and timeout settings became transport-agnostic when USB support was added,
    /// so this remapping keeps existing config files working after an upgrade
    /// </summary>
    private static readonly Dictionary<string, string> RenamedSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SpiBufferSize"] = nameof(Settings.SbcBufferSize),
        ["SpiConnectTimeout"] = nameof(Settings.SbcConnectTimeout),
        ["SpiTransferTimeout"] = nameof(Settings.SbcTransferTimeout),
        ["SpiConnectionTimeout"] = nameof(Settings.SbcConnectionTimeout),
        ["MaxSpiRetries"] = nameof(Settings.MaxSbcRetries)
    };

    /// <summary>
    /// Add settings to the service collection
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration to bind the settings to</param>
    /// <param name="updateOnly">Whether this instance is only launched to update the firmware</param>
    /// <param name="logLevel">Log level to use</param>
    /// <param name="configFile">Path to the configuration file</param>
    /// <param name="socketDirectory">Directory to create the IPC socket in</param>
    /// <param name="socketFile">Name of the IPC socket file</param>
    /// <param name="baseDirectory">Base directory for the virtual SD card</param>
    /// <param name="startErrorFile">Output parameter for the path to the start error file</param>
    /// <returns>Service collection</returns>
    public static IServiceCollection AddSettings(this IServiceCollection services, IConfiguration configuration,
        bool updateOnly, LogLevel? logLevel, FileInfo? configFile, DirectoryInfo? socketDirectory, string? socketFile, DirectoryInfo? baseDirectory,
        out string startErrorFile)
    {
        startErrorFile = configuration.GetValue(nameof(Settings.StartErrorFile), Defaults.StartErrorFile);
        
        // Get log level string and convert it (supports canonical names and short aliases)
        string logLevelString = configuration.GetValue<string>("LogLevel") ?? "Information";
        LogLevel parsedLogLevel = LogLevelHelper.ParseLogLevel(logLevelString);
        
        // Build a memory configuration source that excludes LogLevel (handled above) and
        // remaps renamed keys so config files from earlier DSF versions keep working
        var configData = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var legacyData = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in configuration.AsEnumerable())
        {
            if (kvp.Value is null || kvp.Key == "LogLevel")
            {
                continue;
            }
            // Config files written before the filters became plain patterns hold serialised Regex
            // objects, i.e. <Filter>:<index>:Pattern next to a :Options entry. Keep the pattern and
            // drop the flags, which CompileFilters now applies uniformly
            if (kvp.Key.Contains("Filters:", StringComparison.OrdinalIgnoreCase))
            {
                if (kvp.Key.EndsWith(":Pattern", StringComparison.OrdinalIgnoreCase))
                {
                    configData[kvp.Key[..^":Pattern".Length]] = kvp.Value;
                    continue;
                }
                if (kvp.Key.EndsWith(":Options", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (RenamedSettings.TryGetValue(kvp.Key, out string? currentKey))
            {
                legacyData[currentKey] = kvp.Value;
            }
            else
            {
                configData[kvp.Key] = kvp.Value;
            }
        }

        // Apply legacy values only where the current key was not provided explicitly
        foreach (var kvp in legacyData)
        {
            configData.TryAdd(kvp.Key, kvp.Value);
        }
        var filteredConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();
        
        return services
            .Configure<Settings>(settings => ReplaceConfiguredLists(filteredConfig, settings))
            .Configure<Settings>(filteredConfig)
            .PostConfigure<Settings>(settings =>
            {
                settings.UpdateOnly = updateOnly;
                settings.LogLevel = logLevel ?? parsedLogLevel;
                if (configFile != null)
                {
                    settings.ConfigFile = configFile.FullName;
                }
                if (socketDirectory != null)
                {
                    settings.SocketDirectory = socketDirectory.FullName;
                }
                if (socketFile != null)
                {
                    // Accept either a bare filename or a full path (CLI advertises the latter)
                    if (Path.IsPathRooted(socketFile))
                    {
                        settings.SocketDirectory = Path.GetDirectoryName(socketFile)!;
                        settings.SocketFile = Path.GetFileName(socketFile);
                    }
                    else
                    {
                        settings.SocketFile = socketFile;
                    }
                }
                if (baseDirectory != null)
                {
                    settings.BaseDirectory = baseDirectory.FullName;
                }
                settings.PostConfigure();
            });
    }

    /// <summary>
    /// Empty the list settings that the configuration provides so they are replaced instead of extended
    /// </summary>
    /// <param name="configuration">Configuration holding the settings</param>
    /// <param name="settings">Settings to prepare</param>
    /// <remarks>
    /// The configuration binder adds to an existing list rather than replacing it, so a configured list would
    /// otherwise end up holding the defaults as well. This runs before the binder, and only clears a list that
    /// is actually configured, so unconfigured ones keep their defaults
    /// </remarks>
    private static void ReplaceConfiguredLists(IConfiguration configuration, Settings settings)
    {
        Dictionary<string, List<string>> listSettings = new()
        {
            [nameof(Settings.FirmwareComments)] = settings.FirmwareComments,
            [nameof(Settings.LayerHeightFilters)] = settings.LayerHeightFilters,
            [nameof(Settings.NumLayersFilters)] = settings.NumLayersFilters,
            [nameof(Settings.FilamentFilters)] = settings.FilamentFilters,
            [nameof(Settings.GeneratedByFilters)] = settings.GeneratedByFilters,
            [nameof(Settings.PrintTimeFilters)] = settings.PrintTimeFilters,
            [nameof(Settings.SimulatedTimeFilters)] = settings.SimulatedTimeFilters
        };

        foreach (KeyValuePair<string, List<string>> kvp in listSettings)
        {
            if (configuration.GetSection(kvp.Key).Exists())
            {
                kvp.Value.Clear();
            }
        }
    }
}
