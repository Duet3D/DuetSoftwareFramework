using DuetAPI.Utility;
using NLog;
using NLog.Config;
using NLog.Targets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DuetControlServer
{
    /// <summary>
    /// Settings provider
    /// </summary>
    public static class Settings
    {
        private const string DefaultConfigFile = "/opt/dsf/conf/config.json";
        private const RegexOptions RegexFlags = RegexOptions.IgnoreCase | RegexOptions.Singleline;

        /// <summary>
        /// Indicates if this program is only launched to update the Duet 3 firmware
        /// </summary>
        [JsonIgnore]
        public static bool UpdateOnly { get; set; }

        /// <summary>
        /// Do NOT start the SPI task. This is meant entirely for development purposes and should not be used!
        /// </summary>
        [JsonIgnore]
        public static bool NoSpiTask { get; set; }

        /// <summary>
        /// Path to the configuration file
        /// </summary>
        [JsonIgnore]
        public static string ConfigFilename { get; set; } = DefaultConfigFile;

        /// <summary>
        /// Minimum log level for console output
        /// </summary>
        public static LogLevel LogLevel { get; set; } = LogLevel.Info;

        /// <summary>
        /// Directory in which DSF-related UNIX sockets reside
        /// </summary>
        public static string SocketDirectory { get; set; } = DuetAPI.Connection.Defaults.SocketDirectory;

        /// <summary>
        /// UNIX socket file for DuetControlServer
        /// </summary>
        /// <seealso cref="DuetAPI"/>
        public static string SocketFile { get; set; } = DuetAPI.Connection.Defaults.SocketFile;

        /// <summary>
        /// Fully-qualified path to the main IPC UNIX socket (evaluated during runtime)
        /// </summary>
        [JsonIgnore]
        public static string FullSocketPath { get => Path.Combine(SocketDirectory, SocketFile); }

        /// <summary>
        /// Maximum number of simultaneously pending IPC connections
        /// </summary>
        public static int Backlog { get; set; } = 4;

        /// <summary>
        /// Poll interval for connected IPC clients
        /// </summary>
        public static int SocketPollInterval { get; set; } = 2000;

        /// <summary>
        /// Virtual SD card directory.
        /// Paths starting with 0:/ are mapped to this directory
        /// </summary>
        public static string BaseDirectory { get; set; } = "/opt/dsf/sd";

        /// <summary>
        /// Internal model update interval after which properties of the machine model from
        /// the host controller (e.g. network information and mass storages) are updated (in ms)
        /// </summary>
        public static int HostUpdateInterval { get; set; } = 4000;

        /// <summary>
        /// Maximum time to keep messages in the object model unless client(s) pick them up (in s).
        /// Note that messages are only cleared when the host update task runs.
        /// </summary>
        public static double MaxMessageAge { get; set; } = 60.0;

        /// <summary>
        /// SPI device that is connected to RepRapFirmware
        /// </summary>
        public static string SpiDevice { get; set; } = "/dev/spidev0.0";

        /// <summary>
        /// Frequency to use for SPI transfers (in Hz)
        /// </summary>
        public static int SpiFrequency { get; set; } = 8_000_000;

        /// <summary>
        /// Maximum allowed delay between data exchanges during a full transfer (in ms)
        /// </summary>
        public static int SpiTransferTimeout { get; set; } = 500;

        /// <summary>
        /// Maximum number of sequential transfer retries
        /// </summary>
        public static int MaxSpiRetries { get; set; } = 3;

        /// <summary>
        /// Time to wait after every full transfer (in ms)
        /// </summary>
        public static int SpiPollDelay { get; set; } = 25;

        /// <summary>
        /// Time to wait after every full transfer when simulating a file (in ms)
        /// </summary>
        public static int SpiPollDelaySimulating { get; set; } = 5;

        /// <summary>
        /// Path to the GPIO chip device node
        /// </summary>
        public static string GpioChipDevice { get; set; } = "/dev/gpiochip0";

        /// <summary>
        /// Number of the GPIO pin that is used by RepRapFirmware to flag its ready state
        /// </summary>
        public static int TransferReadyPin { get; set; } = 25;      // Pin 22 on the RaspPi expansion header

        /// <summary>
        /// Number of codes to buffer in the internal print subsystem
        /// </summary>
        public static int BufferedPrintCodes { get; set; } = 32;

        /// <summary>
        /// Number of codes to buffer per macro
        /// </summary>
        public static int BufferedMacroCodes { get; set; } = 16;

        /// <summary>
        /// Maximum space of buffered codes per channel (in bytes)
        /// </summary>
        public static int MaxBufferSpacePerChannel { get; set; } = 1536;

        /// <summary>
        /// Interval of object model updates (in ms)
        /// </summary>
        public static int ModelUpdateInterval { get; set; } = 250;

        /// <summary>
        /// Maximum lock time of the object model. If this time is exceeded, a deadlock is reported and the application is terminated.
        /// Set this to -1 to disable the automatic deadlock detection
        /// </summary>
        public static int MaxMachineModelLockTime { get; set; } = -1;

        /// <summary>
        /// Size of the read buffer used for file parsing in bytes
        /// </summary>
        public static int FileInfoReadBufferSize { get; set; } = 8192;

        /// <summary>
        /// How many bytes to parse max at the beginning of a file to retrieve G-code file information (12KiB)
        /// </summary>
        public static uint FileInfoReadLimitHeader { get; set; } = 12288;

        /// <summary>
        /// How many bytes to parse max at the end of a file to retrieve G-code file information (256KiB)
        /// </summary>
        public static uint FileInfoReadLimitFooter { get; set; } = 262144;

        /// <summary>
        /// Maximum allowed layer height. Used by the file info parser
        /// </summary>
        public static double MaxLayerHeight { get; set; } = 0.9;

        /// <summary>
        /// Regular expressions for finding the layer height (case insensitive)
        /// </summary>
        public static List<Regex> LayerHeightFilters { get; set; } = new List<Regex>
        {
            new Regex(@"\slayer_height\D+(?<mm>(\d+\.?\d*))", RegexFlags),              // Slic3r / Prusa Slicer
            new Regex(@"Layer height\D+(?<mm>(\d+\.?\d*))", RegexFlags),                // Cura
            new Regex(@"layerHeight\D+(?<mm>(\d+\.?\d*))", RegexFlags),                 // Simplify3D
            new Regex(@"layer_thickness_mm\D+(?<mm>(\d+\.?\d*))", RegexFlags),          // KISSlicer and Canvas
            new Regex(@"layerThickness\D+(?<mm>(\d+\.?\d*))", RegexFlags)               // Matter Control
        };

        /// <summary>
        /// Regular expressions for finding the filament consumption (case insensitive, single line)
        /// </summary>
        public static List<Regex> FilamentFilters { get; set; } = new List<Regex>
        {
            new Regex(@"filament used\D+(((?<mm>\d+\.?\d*)mm)(\D+)?)+", RegexFlags),        // Slic3r (mm)
            new Regex(@"filament used\D+(((?<m>\d+\.?\d*)m([^m]|$))(\D+)?)+", RegexFlags),  // Cura (m)
            new Regex(@"filament length\D+(((?<mm>\d+\.?\d*)\s*mm)(\D+)?)+", RegexFlags),   // Simplify3D (mm)
            new Regex(@"filament used \[mm\]\D+(?<mm>\d+\.?\d*)", RegexFlags),              // Prusa Slicer (mm)
            new Regex(@"material\#\d+\D+(?<mm>\d+\.?\d*)", RegexFlags),                     // IdeaMaker (mm)
            new Regex(@"Filament used per extruder:\r\n;\s*(?<name>.+)\s+=\s*(?<mm>[0-9.]+)", RegexFlags)   // Canvas
        };

        /// <summary>
        /// Regular expressions for finding the slicer (case insensitive)
        /// </summary>
        public static List<Regex> GeneratedByFilters { get; set; } = new List<Regex>
        {
            new Regex(@"generated by\s+(.+)", RegexFlags),                              // Slic3r and Simplify3D
            new Regex(@";\s*Sliced by\s+(.+)", RegexFlags),                             // IdeaMaker and Canvas
            new Regex(@";\s*(KISSlicer.*)", RegexFlags),                                // KISSlicer
            new Regex(@";\s*Sliced at:\s*(.+)", RegexFlags),                            // Cura (old)
            new Regex(@";\s*Generated with\s*(.+)", RegexFlags)                         // Cura (new)
        };

        /// <summary>
        /// Regular expressions for finding the print time
        /// </summary>
        public static List<Regex> PrintTimeFilters { get; set; } = new List<Regex>
        {
            new Regex(@"estimated printing time = ((?<h>(\d+))h\s*)?((?<m>(\d+))m\s*)?((?<s>(\d+))s)?", RegexFlags),                                     // Slic3r PE
            new Regex(@";TIME:(?<s>(\d+\.?\d*))", RegexFlags),                                                                                           // Cura
            new Regex(@"Build time: ((?<h>\d+) hours\s*)?((?<m>\d+) minutes\s*)?((?<s>(\d+) seconds))?", RegexFlags),                                    // Simplify3D
            new Regex(@"Estimated Build Time:\s+((?<h>(\d+\.?\d*)) hours\s*)?((?<m>(\d+\.?\d*)) minutes\s*)?((?<s>(\d+\.?\d*)) seconds)?", RegexFlags)   // KISSlicer and Canvas
        };

        /// <summary>
        /// Regular expressions for finding the simulated time
        /// </summary>
        public static List<Regex> SimulatedTimeFilters { get; set; } = new List<Regex>
        {
            new Regex(@"; Simulated print time\D+(?<s>(\d+\.?\d*))", RegexFlags)
        };

        /// <summary>
        /// Initialize settings and load them from the config file or create it if it does not exist
        /// </summary>
        /// <returns>False if the application is supposed to terminate</returns>
        public static bool Init(string[] args)
        {
            // Check if a custom config is supposed to be loaded
            string lastArg = null;
            foreach (string arg in args)
            {
                if (lastArg == "-c" || lastArg == "--config")
                {
                    ConfigFilename = arg;
                }
                else if (arg == "-h" || arg == "--help")
                {
                    Console.WriteLine("Available command line arguments:");
                    Console.WriteLine("-u, --update: Update RepRapFirmware and exit");
                    Console.WriteLine("-l, --log-level [trace,debug,info,warn,error,fatal,off]: Set minimum log level");
                    Console.WriteLine("-c, --config: Override path to the JSON config file");
                    Console.WriteLine("-S, --socket-directory: Specify the UNIX socket directory");
                    Console.WriteLine("-s, --socket-file: Specify the UNIX socket file");
                    Console.WriteLine("-u, --socket-user <user>: Specify the owning user of the UNIX socket file");
                    Console.WriteLine("-g, --socket-group <group>: Specify the owning group of the UNIX socket file");
                    Console.WriteLine("-b, --base-directory: Set the virtual SD card base directory");
                    // Console.WriteLine("--no-spi-task: Do NOT start the SPI task. This is only intended for development purposes!");
                    Console.WriteLine("-h, --help: Display this text");
                    return false;
                }
                lastArg = arg;
            }

            // See if the file exists and attempt to load the settings from it, otherwise create it
            if (File.Exists(ConfigFilename))
            {
                LoadFromFile(ConfigFilename);
                ParseParameters(args);
            }
            else
            {
                ParseParameters(args);
                SaveToFile(ConfigFilename);
            }

            // Initialize logging
            LoggingConfiguration logConfig = new LoggingConfiguration();
            ColoredConsoleTarget logConsoleTarget = new ColoredConsoleTarget
            {
                // Create a layout for messages like:
                // [trace] Really verbose stuff
                // [debug] Verbose debugging stuff
                // [info] This is a regular log message
                // [warning] Something not too nice
                // [error] IPC#3: This is an IPC error message
                //         System.Exception: Foobar
                //         at { ... }
                // [error] That is some other error message
                //         System.Exception: Yada yada
                //         at { ... }
                // [fatal] System.Exception: Blah blah
                //         at { ... }
                Layout = @"[${level:lowercase=true}] ${when:when=!contains('${logger}','.') and !ends-with('${logger}','.g'):inner=${logger}${literal:text=\:} }${message}${onexception:when='${message}'!='${exception:format=ToString}'):${newline}   ${exception:format=ToString}}"
            };
            logConfig.AddRule(LogLevel, LogLevel.Fatal, logConsoleTarget);
            LogManager.AutoShutdown = false;
            LogManager.Configuration = logConfig;

            // Go on
            return true;
        }

        /// <summary>
        /// Parse the command line parameters
        /// </summary>
        /// <param name="args">Command-line arguments</param>
        private static void ParseParameters(string[] args)
        {
            string lastArg = null;
            foreach (string arg in args)
            {
                if (lastArg == "-l" || lastArg == "--log-level")
                {
                    LogLevel = LogLevel.FromString(arg);
                }
                else if (lastArg == "-S" || lastArg == "--socket-directory")
                {
                    SocketDirectory = arg;
                }
                else if (lastArg == "-s" || lastArg == "--socket-file")
                {
                    SocketFile = arg;
                }
                else if (lastArg == "-b" || lastArg == "--base-directory")
                {
                    BaseDirectory = arg;
                }
                else if (arg == "-u" || lastArg == "--update")
                {
                    UpdateOnly = true;
                }
                else if (arg == "--no-spi-task")
                {
                    NoSpiTask = true;
                }
                lastArg = arg;
            }
        }

        /// <summary>
        /// Load the settings from a given file
        /// </summary>
        /// <param name="fileName">File to load the settings from</param>
        private static void LoadFromFile(string fileName)
        {
            byte[] content;
            using (FileStream fileStream = new FileStream(fileName, FileMode.Open, FileAccess.Read))
            {
                content = new byte[fileStream.Length];
                fileStream.Read(content, 0, (int)fileStream.Length);
            }

            Utf8JsonReader reader = new Utf8JsonReader(content);
            PropertyInfo property = null;
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        string propertyName = reader.GetString();
                        property = typeof(Settings).GetProperty(propertyName, BindingFlags.Static | BindingFlags.Public | BindingFlags.IgnoreCase);
                        if (property == null || Attribute.IsDefined(property, typeof(JsonIgnoreAttribute)))
                        {
                            // Skip non-existent and ignored properties
                            if (reader.Read())
                            {
                                if (reader.TokenType == JsonTokenType.StartArray)
                                {
                                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) { }
                                }
                                else if (reader.TokenType == JsonTokenType.StartObject)
                                {
                                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject) { }
                                }
                            }
                        }
                        break;

                    case JsonTokenType.Number:
                        if (property.PropertyType == typeof(int))
                        {
                            property.SetValue(null, reader.GetInt32());
                        }
                        else if (property.PropertyType == typeof(uint))
                        {
                            property.SetValue(null, reader.GetUInt32());
                        }
                        else if (property.PropertyType == typeof(float))
                        {
                            property.SetValue(null, reader.GetSingle());
                        }
                        else if (property.PropertyType == typeof(double))
                        {
                            property.SetValue(null, reader.GetDouble());
                        }
                        else
                        {
                            throw new JsonException($"Bad number type: {property.PropertyType.Name}");
                        }
                        break;

                    case JsonTokenType.String:
                        if (property.PropertyType == typeof(string))
                        {
                            property.SetValue(null, reader.GetString());
                        }
                        else if (property.PropertyType == typeof(LogLevel))
                        {
                            property.SetValue(null, LogLevel.FromString(reader.GetString()));
                        }
                        else
                        {
                            throw new JsonException($"Bad string type: {property.PropertyType.Name}");
                        }
                        break;

                    case JsonTokenType.StartArray:
                        if (property.PropertyType == typeof(List<Regex>))
                        {
                            JsonRegexListConverter regexListConverter = new JsonRegexListConverter();
                            property.SetValue(null, regexListConverter.Read(ref reader, typeof(List<Regex>), null));
                        }
                        else
                        {
                            throw new JsonException($"Bad list type: {property.PropertyType.Name}");
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Save the settings to a given file
        /// </summary>
        /// <param name="fileName">File to save the settings to</param>
        private static void SaveToFile(string fileName)
        {
            using FileStream fileStream = new FileStream(fileName, FileMode.Create, FileAccess.Write);
            using Utf8JsonWriter writer = new Utf8JsonWriter(fileStream, new JsonWriterOptions() { Indented = true });

            writer.WriteStartObject();
            foreach (PropertyInfo property in typeof(Settings).GetProperties(BindingFlags.Static | BindingFlags.Public))
            {
                if (!Attribute.IsDefined(property, typeof(JsonIgnoreAttribute)))
                {
                    object value = property.GetValue(null);
                    if (value is string stringValue)
                    {
                        writer.WriteString(property.Name, stringValue);
                    }
                    else if (value is int intValue)
                    {
                        writer.WriteNumber(property.Name, intValue);
                    }
                    else if (value is uint uintValue)
                    {
                        writer.WriteNumber(property.Name, uintValue);
                    }
                    else if (value is float floatValue)
                    {
                        writer.WriteNumber(property.Name, floatValue);
                    }
                    else if (value is double doubleValue)
                    {
                        writer.WriteNumber(property.Name, doubleValue);
                    }
                    else if (value is List<Regex> regexList)
                    {
                        writer.WritePropertyName(property.Name);

                        JsonRegexListConverter regexListConverter = new JsonRegexListConverter();
                        regexListConverter.Write(writer, regexList, null);
                    }
                    else if (value is LogLevel logLevelValue)
                    {
                        writer.WriteString(property.Name, logLevelValue.ToString().ToLowerInvariant());
                    }
                    else
                    {
                        throw new JsonException($"Unknown value type {property.PropertyType.Name}");
                    }
                }
            }
            writer.WriteEndObject();
        }
    }
}
