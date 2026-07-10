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
    /// Poll interval for connected IPC clients (in ms)
    /// </summary>
    public int SocketPollInterval { get; set; } = 2000;

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
    /// Communication method to use for connection to RepRapFirmware
    /// </summary>
    public CommunicationMethod CommunicationMethod { get; set; } = CommunicationMethod.SPI;

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
    /// The CPU core which has been isolated from the OS scheduler
    /// </summary>
    public int IsolatedCoreId { get; set; } = 3;

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
    /// Maximum space of buffered codes per channel (in bytes)
    /// </summary>
    public int MaxBufferSpacePerChannel { get; set; } = 1536;

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
    public List<Regex> LayerHeightFilters { get; set; } =
    [
        new(@"^\s*layer_height\D+(?<mm>(\d+\.?\d*))", DefaultRegexFlags),            // Slic3r / Prusa Slicer
        new(@"Layer height\D+(?<mm>(\d+\.?\d*))", DefaultRegexFlags),                // Cura
        new(@"layerHeight\D+(?<mm>(\d+\.?\d*))", DefaultRegexFlags),                 // Simplify3D
        new(@"layer_thickness_mm\D+(?<mm>(\d+\.?\d*))", DefaultRegexFlags),          // KISSlicer and Canvas
        new(@"layerThickness\D+(?<mm>(\d+\.?\d*))", DefaultRegexFlags),              // Matter Control
        new(@"sliceHeight\D+(?<mm>(\d+\.?\d*))", DefaultRegexFlags)                  // Kiri:Moto
    ];

    /// <summary>
    /// Regular expressions for finding the total number of layers
    /// </summary>
    /// <remarks>
    /// If the number of layers cannot be found, the total number of layers is calculated from the layer and object heights (if applicable)
    /// </remarks>
    public List<Regex> NumLayersFilters { get; set; } =
    [
        new(@"NUM_LAYERS\D+(\d+)", DefaultRegexFlags)
    ];

    /// <summary>
    /// Regular expressions for finding the filament consumption (case insensitive, single line)
    /// </summary>
    public List<Regex> FilamentFilters { get; set; } =
    [
        new(@"filament used\D+(((?<mm>\d+\.?\d*)\s*mm)(\D+)?)+", DefaultRegexFlags),                     // Slic3r and Kiri:Moto (mm)
        new(@"filament used\D+(((?<m>\d+\.?\d*)m([^m]|$))(\D+)?)+", DefaultRegexFlags),                  // Cura (m)
        new(@"filament length\D+(((?<mm>\d+\.?\d*)\s*mm)(\D+)?)+", DefaultRegexFlags),                   // Simplify3D (mm)
        new(@"filament used \[mm\]\D+((?<mm>\d+\.?\d*)(\D+)?)+", DefaultRegexFlags),                     // Prusa Slicer (mm)
        new(@"material\#(?<index>\d+)\D+(?<mm>\d+\.?\d*)", DefaultRegexFlags),                           // IdeaMaker (mm)
        new(@"Ext\s*\#\d+\D+(?<mm>\d+\.?\d*)", DefaultRegexFlags),                                       // KISSSlicer v2.0 (mm)
        new(@"Filament used per extruder:\r\n;\s*(?<name>.+)\s+=\s*(?<mm>[0-9.]+)", DefaultRegexFlags),  // Canvas
        new(@"filament used extruder (?<index>\d+) \(mm\) = (?<mm>\d+\.?\d*)", DefaultRegexFlags)        // MatterControl v2
    ];

    /// <summary>
    /// Regular expressions for finding the slicer (case insensitive)
    /// </summary>
    public List<Regex> GeneratedByFilters { get; set; } =
    [
        new(@"generated by\s+(.+)", DefaultRegexFlags),                              // Slic3r, Simplify3D, Kiri:Moto
        new(@"Sliced by\s+(.+)", DefaultRegexFlags),                                 // IdeaMaker and Canvas
        new(@"(KISSlicer.*)", DefaultRegexFlags),                                    // KISSlicer
        new(@"Sliced at:\s*(.+)", DefaultRegexFlags),                                // Cura (old)
        new(@"Generated with\s*(.+)", DefaultRegexFlags)                             // Cura (new)
    ];

    /// <summary>
    /// Regular expressions for finding the print time
    /// </summary>
    public List<Regex> PrintTimeFilters { get; set; } =
    [
        new(@"estimated printing time .*= ((?<d>(\d+))d\s*)?((?<h>(\d+))h\s*)?((?<m>(\d+))m\s*)?((?<s>(\d+))s)?", DefaultRegexFlags),                // Slic3r PE
        new(@"TIME:(?<s>(\d+\.?\d*))", DefaultRegexFlags),                                                                                           // Cura
        new(@"Build Time:\s+((?<h>(\d+\.?\d*)) hour(s)?\s*)?((?<m>(\d+\.?\d*)) minute(s)?\s*)?((?<s>(\d+\.?\d*)) second(s)?)?", DefaultRegexFlags),  // Simplify3D, KISSlicer, Canvas, IceSL
        new(@"print time:\s+(?<s>(\d+\.?\d*))(s)?", DefaultRegexFlags),                                                                              // Kiri:Moto, and IdeaMaker v4
        new(@"Total estimated \(pre-cool\) minutes: ((?<m>\d+\.?\d*))", DefaultRegexFlags),                                                          // KISSlicer v2.0
        new(@"total print time \(s\) = (?<s>(\d+\.?\d*))", DefaultRegexFlags),                                                                       // MatterControl v2
        new(@"Build time:\s+(?<h>(\d+\.?\d*)):(?<m>(\d+\.?\d*)):(?<s>(\d+\.?\d*))", DefaultRegexFlags)                                               // REACTOR
    ];

    /// <summary>
    /// Regular expressions for finding the simulated time
    /// </summary>
    public List<Regex> SimulatedTimeFilters { get; set; } =
    [
        new(@"Simulated print time\D+(?<s>(\d+\.?\d*))", DefaultRegexFlags)
    ];

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
        JsonSerializer.Serialize(fileStream, this, new JsonSerializerOptions()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
            Converters = { new RegexJsonConverter() }
        });
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
}
