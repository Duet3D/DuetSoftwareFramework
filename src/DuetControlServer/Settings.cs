using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using DuetAPI.Utility;
using Newtonsoft.Json;

namespace DuetControlServer
{
    /// <summary>
    /// Settings provider
    /// </summary>
    /// <remarks>This class cannot be static because JSON.NET requires an instance for deserialization</remarks>
    public /*static*/ class Settings
    {
        private static readonly string DefaultConfigFile = "/opt/dsf/conf/config.json";
        private const RegexOptions RegexFlags = RegexOptions.IgnoreCase | RegexOptions.Singleline;

        /// <summary>
        /// Path to the UNIX socket for IPC
        /// </summary>
        /// <seealso cref="DuetAPI"/>
        [JsonProperty]
        public static string SocketPath { get; set; } = DuetAPI.Connection.Defaults.SocketPath;

        /// <summary>
        /// Maximum number of pending IPC connection
        /// </summary>
        [JsonProperty]
        public static int Backlog { get; set; } = 4;

        /// <summary>
        /// Virtual SD card directory.
        /// Paths starting with 0:/ are mapped to this directory
        /// </summary>
        [JsonProperty]
        public static string BaseDirectory { get; set; } = "/opt/dsf/sd";

        /// <summary>
        /// Internal model update interval after which properties of the machine model from
        /// the host controller (e.g. network information and mass storages) are updated (in ms)
        /// </summary>
        [JsonProperty]
        public static int HostUpdateInterval { get; set; } = 4000;

        /// <summary>
        /// Maximum time to keep messages in the object model unless client(s) pick them up (in s).
        /// Note that messages are only cleared when the host update task runs.
        /// </summary>
        [JsonProperty]
        public static double MaxMessageAge { get; set; } = 60.0;

        /// <summary>
        /// Frequency to use for SPI transfers
        /// </summary>
        [JsonProperty]
        public static int SpiFrequency = 2_000_000;

        /// <summary>
        /// Bus ID of the SPI device that is connected to RepRapFirmware (on Linux the format is /dev/spidev{bus}.{csline})
        /// </summary>
        [JsonProperty]
        public static int SpiBusID { get; set; } = 0;

        /// <summary>
        /// Chip select line of the SPI device that is connected to RepRapFirmware (on Linux the format is /dev/spidev{bus}.{csline})
        /// </summary>
        [JsonProperty]
        public static int SpiChipSelectLine { get; set; } = 0;

        /// <summary>
        /// Number of iterations to spin-wait before the transfer ready pin is polled
        /// </summary>
        public static int SpiSpinIterations { get; set; } = 3000;

        /// <summary>
        /// Maximum allowed delay between data exchanges during a full transfer (in ms)
        /// </summary>
        [JsonProperty]
        public static int SpiTransferTimeout { get; set; } = 500;

        /// <summary>
        /// Maximum number of sequential transfer retries
        /// </summary>
        [JsonProperty]
        public static int MaxSpiRetries { get; set; } = 3;

        /// <summary>
        /// Time to wait after every transfer (in ms)
        /// </summary>
        [JsonProperty]
        public static int SpiPollDelay { get; set; } = 20;

        /// <summary>
        /// Number of the GPIO pin that is used by RepRapFirmware to flag its ready state
        /// </summary>
        [JsonProperty]
        public static int TransferReadyPin { get; set; } = 25;      // Pin 22 on the RaspPi expansion header

        /// <summary>
        /// Maximum delay after which a status update is requested even when in burst mode (in ms)
        /// </summary>
        [JsonProperty]
        public static double MaxUpdateDelay { get; set; } = 250.0;

        /// <summary>
        /// How many bytes to parse max at the beginning and end of a file to retrieve G-code file information
        /// </summary>
        [JsonProperty]
        public static uint FileInfoReadLimit { get; set; } = 32768;

        /// <summary>
        /// Maximum allowed layer height. Used by the file info parser
        /// </summary>
        [JsonProperty]
        public static double MaxLayerHeight { get; set; } = 0.9;

        /// <summary>
        /// Regular expressions for finding the layer height (case insensitive)
        /// </summary>
        [JsonProperty]
        public static List<Regex> LayerHeightFilters { get; set; } = new List<Regex>
        {
            new Regex(@"layer_height\D+(?<mm>(\d+\.?\d*))", RegexFlags),                // Slic3r
            new Regex(@"Layer height\D+(?<mm>(\d+\.?\d*))", RegexFlags),                // Cura
            new Regex(@"layerHeight\D+(?<mm>(\d+\.?\d*))", RegexFlags),                 // Simplify3D
            new Regex(@"layer_thickness_mm\D+(?<mm>(\d+\.?\d*))", RegexFlags),          // KISSlicer
            new Regex(@"layerThickness\D+(?<mm>(\d+\.?\d*))", RegexFlags)               // Matter Control
        };

        /// <summary>
        /// Regular expressions for finding the filament consumption (case insensitive, single line)
        /// </summary>
        [JsonProperty]
        public static List<Regex> FilamentFilters { get; set; } = new List<Regex>
        {
            new Regex(@"filament used\D+(((?<mm>\d+\.?\d*)mm)(\D+)?)+", RegexFlags),        // Slic3r (mm)
            new Regex(@"filament used\D+(((?<m>\d+\.?\d*)m([^m]|$))(\D+)?)+", RegexFlags),  // Cura (m)
            new Regex(@"material\#\d+\D+(?<mm>\d+\.?\d*)", RegexFlags),                     // IdeaMaker (mm)
            new Regex(@"filament length\D+(((?<mm>\d+\.?\d*)\s*mm)(\D+)?)+", RegexFlags)    // Simplify3D (mm)
        };

        /// <summary>
        /// Regular expressions for finding the slicer (case insensitive)
        /// </summary>
        [JsonProperty]
        public static List<Regex> GeneratedByFilters { get; set; } = new List<Regex>
        {
            new Regex(@"generated by\s+(.+)", RegexFlags),                              // Slic3r and Simplify3D
            new Regex(@";\s*Sliced by\s+(.+)", RegexFlags),                             // IdeaMaker
            new Regex(@";\s*(KISSlicer.*)", RegexFlags),                                // KISSlicer
            new Regex(@";\s*Sliced at:\s*(.+)", RegexFlags),                            // Cura (old)
            new Regex(@";\s*Generated with\s*(.+)", RegexFlags)                         // Cura (new)
        };

        /// <summary>
        /// Regular expressions for finding the print time
        /// </summary>
        [JsonProperty]
        public static List<Regex> PrintTimeFilters { get; set; } = new List<Regex>
        {
            new Regex(@"estimated printing time = ((?<h>(\d+))h\s*)?((?<m>(\d+))m\s*)?((?<s>(\d+))s)?", RegexFlags),                                     // Slic3r PE
            new Regex(@";TIME:(?<s>(\d+\.?\d*))", RegexFlags),                                                                                           // Cura
            new Regex(@"Build time: ((?<h>\d+) hours\s*)?((?<m>\d+) minutes\s*)?((?<s>(\d+) seconds))?", RegexFlags),                                    // Simplify3D
            new Regex(@"Estimated Build Time:\s+((?<h>(\d+\.?\d*)) hours\s*)?((?<m>(\d+\.?\d*)) minutes\s*)?((?<s>(\d+\.?\d*)) seconds)?", RegexFlags)   // KISSlicer
        };

        /// <summary>
        /// Regular expressions for finding the simulated time
        /// </summary>
        [JsonProperty]
        public static List<Regex> SimulatedTimeFilters { get; set; } = new List<Regex>
        {
            new Regex(@"; Simulated print time\D+(?<s>(\d+\.?\d*))", RegexFlags)
        };

        /// <summary>
        /// Load settings from the config file or create it if it does not already exist
        /// </summary>
        /// <param name="args">Command-line arguments</param>
        internal static void Load(string[] args)
        {
            // Attempt to parse the config file path from the command-line arguments
            string lastArg = null, config = DefaultConfigFile;
            foreach (string arg in args)
            {
                if (lastArg == "-s" || lastArg == "--config")
                {
                    config = arg;
                    break;
                }
                lastArg = arg;
            }
            
            // See if the file exists and attempt to load the settings from it, otherwise create it
            if (File.Exists(config))
            {
                string fileContent = File.ReadAllText(config);
                LayerHeightFilters.Clear();
                FilamentFilters.Clear();
                GeneratedByFilters.Clear();
                PrintTimeFilters.Clear();
                SimulatedTimeFilters.Clear();
                JsonConvert.DeserializeObject<Settings>(fileContent);
            }
            else
            {
                string defaultSettings = JsonConvert.SerializeObject(new Settings(), Formatting.Indented);
                File.WriteAllText(config, defaultSettings);
            }
            
            // Parse other command-line parameters
            ParseParameters(args);
        }

        private static void ParseParameters(string[] args)
        {
            string lastArg = null;
            foreach (string arg in args)
            {
                if (arg == "-i" || arg == "--info")
                {
                    Console.WriteLine("-i, --info: Display this reference");
                    Console.WriteLine("-s, --config: Set config file");
                    Console.WriteLine("-S, --socket: Specify the UNIX socket path");
                    Console.WriteLine("-b, --base-directory: Set the base path for system and G-code files");
                }
                else if (lastArg == "-S" || lastArg == "--socket")
                {
                    SocketPath = arg;
                }
                else if (lastArg == "-b" || lastArg == "--base-directory")
                {
                    BaseDirectory = arg;
                }
                lastArg = arg;
            }
        }
    }
}
