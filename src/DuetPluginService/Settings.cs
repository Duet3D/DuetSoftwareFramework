using NLog;
using NLog.Config;
using NLog.Targets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetPluginService
{
    /// <summary>
    /// Class holding the settings of this application
    /// </summary>
    public static class Settings
    {
        private const string DefaultConfigFile = "/opt/dsf/conf/plugins.json";

        /// <summary>
        /// Path to the configuration file
        /// </summary>
        [JsonIgnore]
        public static string ConfigFilename { get; set; } = DefaultConfigFile;

        /// <summary>
        /// Path to the UNIX socket provided by DuetControlServer
        /// </summary>
        public static string SocketPath { get; set; } = DuetAPI.Connection.Defaults.FullSocketPath;

        /// <summary>
        /// Minimum log level for console output
        /// </summary>
        public static LogLevel LogLevel { get; set; } = LogLevel.Info;

        /// <summary>
        /// Directory holding DSF plugins
        /// </summary>
        public static string PluginDirectory { get; set; } = "/opt/dsf/plugins";

        /// <summary>
        /// Virtual SD card directory.
        /// Paths starting with 0:/ are mapped to this directory
        /// </summary>
        public static string BaseDirectory { get; set; } = "/opt/dsf/sd";

        /// <summary>
        /// Disable AppArmor security policy generation (not recommended, potential security hazard)
        /// </summary>
        public static bool DisableAppArmor { get; set; }

        /// <summary>
        /// Path to the utility that allows profile management
        /// </summary>
        public static string AppArmorParser { get; set; } = "/usr/sbin/apparmor_parser";

        /// <summary>
        /// Directory holding AppArmor security profiles
        /// </summary>
        public static string AppArmorTemplate { get; set; } = "/opt/dsf/conf/apparmor.conf";

        /// <summary>
        /// Directory holding AppArmor security profiles
        /// </summary>
        public static string AppArmorProfileDirectory { get; set; } = "/etc/apparmor.d";

        /// <summary>
        /// Command to run before installing third-party packages
        /// </summary>
        public static string PreinstallPackageCommand { get; set; } = "/usr/bin/apt";


        /// <summary>
        /// Command-line arguments to use before installing third-party packages
        /// </summary>
        public static string PreinstallPackageArguments { get; set; } = "update";

        /// <summary>
        /// Command to install third-party packages
        /// </summary>
        public static string InstallPackageCommand { get; set; } = "/usr/bin/apt";

        /// <summary>
        /// Command-line arguments to install third-party packages
        /// </summary>
        public static string InstallPackageArguments { get; set; } = "install -y {package}";

        /// <summary>
        /// Command to install third-party Python packages
        /// </summary>
        public static string InstallPythonPackageCommand { get; set; } = "/usr/bin/pip3";

        /// <summary>
        /// Command-line arguments to install third-party Python packages
        /// </summary>
        public static string InstallPythonPackageArguments { get; set; } = "install {package}";

        /// <summary>
        /// Environment variables for the installation command
        /// </summary>
        public static Dictionary<string, string> InstallPackageEnvironment { get; set; } = new Dictionary<string, string>()
        {
            { "DEBIAN_FRONTEND", "noninteractive"  }
        };

        /// <summary>
        /// Command to install a local package
        /// </summary>
        public static string InstallLocalPackageCommand { get; set; } = "/usr/bin/dpkg";

        /// <summary>
        /// Command-line arguments to install a local package
        /// </summary>
        public static string InstallLocalPackageArguments { get; set; } = "--force-confold -i {file}";

        /// <summary>
        /// Command to uninstall a local package
        /// </summary>
        public static string UninstallLocalPackageCommand { get; set; } = "/usr/bin/dpkg";

        /// <summary>
        /// Command-line arguments to uninstall a local package
        /// </summary>
        public static string UninstallLocalPackageArguments { get; set; } = "-r {package}";

        /// <summary>
        /// Timeout in ms for SIGTERM requests. When it expires plugin processes are forcefully killed
        /// </summary>
        public static int StopTimeout { get; set; } = 4000;

        /// <summary>
        /// Initialize settings and load them from the config file or create it if it does not exist
        /// </summary>
        /// <returns>False if the application is supposed to terminate</returns>
        public static bool Init(string[] args)
        {
            // Check if a custom config is supposed to be loaded
            string? lastArg = null;
            foreach (string arg in args)
            {
                if (lastArg is "-c" or "--config")
                {
                    ConfigFilename = arg;
                }
                else if (arg is "-h" or "--help")
                {
                    Console.WriteLine("Available command line arguments:");
                    Console.WriteLine("-l, --log-level [trace,debug,info,warn,error,fatal,off]: Set minimum log level");
                    Console.WriteLine("-c, --config: Override path to the JSON config file");
                    Console.WriteLine("-s, --socket: Specify the UNIX socket file");
                    Console.WriteLine("-h, --help: Display this text");
                    return false;
                }
                lastArg = arg;
            }

            // See if the file exists and attempt to load the settings from it, otherwise create it
            try
            {
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
            }
            finally
            {
                // Initialize logging
                LoggingConfiguration logConfig = new();
                ColoredConsoleTarget logConsoleTarget = new()
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
                    Layout = @"[${level:lowercase=true}] ${logger}${literal:text=\:} ${message}${onexception:when='${message}'!='${exception:format=ToString}'):${newline}   ${exception:format=ToString}}"
                };
                logConfig.AddRule(LogLevel, LogLevel.Fatal, logConsoleTarget);
                LogManager.AutoShutdown = false;
                LogManager.Configuration = logConfig;
            }

            // Go on
            return true;
        }

        /// <summary>
        /// Parse the command line parameters
        /// </summary>
        /// <param name="args">Command-line arguments</param>
        private static void ParseParameters(string[] args)
        {
            string? lastArg = null;
            foreach (string arg in args)
            {
                if (lastArg is "-l" or "--log-level")
                {
                    LogLevel = LogLevel.FromString(arg);
                }
                else if (lastArg is "-s" or "--socket")
                {
                    SocketPath = arg;
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
            using (FileStream fileStream = new(fileName, FileMode.Open, FileAccess.Read))
            {
                content = new byte[fileStream.Length];
                fileStream.Read(content, 0, (int)fileStream.Length);
            }

            Utf8JsonReader reader = new(content);
            PropertyInfo? property = null;
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        string propertyName = reader.GetString()!;
                        property = typeof(Settings).GetProperty(propertyName, BindingFlags.Static | BindingFlags.Public | BindingFlags.IgnoreCase);
                        if (property is null || Attribute.IsDefined(property, typeof(JsonIgnoreAttribute)))
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

                    case JsonTokenType.True:
                    case JsonTokenType.False:
                        if (property!.PropertyType == typeof(bool))
                        {
                            property.SetValue(null, reader.GetBoolean());
                        }
                        else
                        {
                            throw new JsonException($"Bad boolean type: {property.PropertyType.Name}");
                        }
                        break;

                    case JsonTokenType.Number:
                        if (property!.PropertyType == typeof(int))
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
                        if (property!.PropertyType == typeof(string))
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
                        if (property!.PropertyType == typeof(List<string>))
                        {
                            List<string> list = [];
                            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                            {
                                list.Add(reader.GetString()!);
                            }
                            property.SetValue(null, list);
                        }
                        else
                        {
                            throw new JsonException($"Bad list type: {property.PropertyType.Name}");
                        }
                        break;

                    case JsonTokenType.StartObject:
                        if (property is not null)
                        {
                            if (property.PropertyType == typeof(Dictionary<string, string>))
                            {
                                Dictionary<string, string> dict = [];
                                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                                {
                                    dict.Add(reader.GetString()!, reader.GetString()!);
                                }
                                property.SetValue(null, dict);
                            }
                            else
                            {
                                throw new JsonException($"Bad object type: {property.PropertyType.Name}");
                            }
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
            using FileStream fileStream = new(fileName, FileMode.Create, FileAccess.Write);
            using Utf8JsonWriter writer = new(fileStream, new JsonWriterOptions()
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Indented = true
            });

            writer.WriteStartObject();
            foreach (PropertyInfo property in typeof(Settings).GetProperties(BindingFlags.Static | BindingFlags.Public))
            {
                if (!Attribute.IsDefined(property, typeof(JsonIgnoreAttribute)))
                {
                    object? value = property.GetValue(null);
                    if (value is string stringValue)
                    {
                        writer.WriteString(property.Name, stringValue);
                    }
                    else if (value is bool boolValue)
                    {
                        writer.WriteBoolean(property.Name, boolValue);
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
                    else if (value is List<string> stringList)
                    {
                        writer.WritePropertyName(property.Name);
                        writer.WriteStartArray();
                        foreach (string item in stringList)
                        {
                            writer.WriteStringValue(item);
                        }
                        writer.WriteEndArray();
                    }
                    else if (value is LogLevel logLevelValue)
                    {
                        writer.WriteString(property.Name, logLevelValue.ToString().ToLowerInvariant());
                    }
                    else if (value is Dictionary<string, string> dict)
                    {
                        writer.WritePropertyName(property.Name);
                        writer.WriteStartObject();
                        foreach (var kv in dict)
                        {
                            writer.WriteString(kv.Key, kv.Value);
                        }
                        writer.WriteEndObject();
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
