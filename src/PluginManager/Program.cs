using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetAPIClient;
using System;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

namespace PluginManager
{
    public static class Program
    {
        private enum PluginOperation
        {
            List,
            ListData,
            Install,
            Reload,
            Start,
            SetData,
            Stop,
            Uninstall,
            IsInstalled,
            IsStarted
        }

        /// <summary>
        /// Set to true if no regular messages are supposed to be printed
        /// </summary>
        private static bool _quiet;

        private static void WriteLine(string format, params object[] arg)
        {
            if (!_quiet)
            {
                Console.WriteLine(format, arg);
            }
        }


        /// <summary>
        /// Entry point of this application
        /// </summary>
        /// <param name="args">Command-line arguments</param>
        /// <returns>Return code</returns>
        public static async Task<int> Main(string[] args)
        {
            // Parse the command line arguments
            PluginOperation operation = PluginOperation.List;
            string? lastArg = null, mainArg = null, socketPath = Defaults.FullSocketPath;
            foreach (string arg in args)
            {
                switch (lastArg)
                {
                    case "-s" or "--socket":
                        socketPath = arg;
                        break;
                    case "install":
                        operation = PluginOperation.Install;
                        mainArg = arg;
                        break;
                    case "reload":
                        operation = PluginOperation.Reload;
                        mainArg = arg;
                        break;
                    case "start":
                        operation = PluginOperation.Start;
                        mainArg = arg;
                        break;
                    case "set-data":
                        operation = PluginOperation.SetData;
                        mainArg = arg;
                        break;
                    case "stop":
                        operation = PluginOperation.Stop;
                        mainArg = arg;
                        break;
                    case "uninstall":
                        operation = PluginOperation.Uninstall;
                        mainArg = arg;
                        break;
                    case "is-installed":
                        operation = PluginOperation.IsInstalled;
                        mainArg = arg;
                        break;
                    case "is-started":
                        operation = PluginOperation.IsStarted;
                        mainArg = arg;
                        break;
                    default:
                        if (arg == "list")
                        {
                            operation = PluginOperation.List;
                        }
                        else if (arg == "list-data")
                        {
                            operation = PluginOperation.ListData;
                        }
                        else if (arg == "-q" || arg == "--quiet")
                        {
                            _quiet = true;
                        }
                        else if (arg == "-h" || arg == "--help")
                        {
                            Console.WriteLine("Available command line arguments:");
                            Console.WriteLine("list: List plugin status (default)");
                            Console.WriteLine("list-data: List plugin data");
                            Console.WriteLine("install <zipfile>: Install new ZIP bundle");
                            Console.WriteLine("reload <id>: Reload a plugin manifest");
                            Console.WriteLine("start <id>: Start a plugin");
                            Console.WriteLine("set-data <id>:<key>=<value>: Set plugin data (JSON or text)");
                            Console.WriteLine("stop <id>: Stop a plugin");
                            Console.WriteLine("uninstall <id>: Uninstall a plugin");
                            Console.WriteLine("is-installed <id>: Check if a plugin is installed (result is given by return code)");
                            Console.WriteLine("is-started <id>: Check if a plugin is started (result is given by return code)");
                            Console.WriteLine("-s, --socket <socket>: UNIX socket to connect to");
                            Console.WriteLine("-q, --quiet: Do not output regular messages");
                            Console.WriteLine("-h, --help: Display this help text");
                            return 0;
                        }
                        break;
                }
                lastArg = arg;
            }

            // Create a new connection and connect to DuetControlServer
            using CommandConnection connection = new();
            try
            {
                await connection.Connect(socketPath);
            }
            catch (SocketException)
            {
                if (!_quiet)
                {
                    Console.Error.WriteLine("Failed to connect to DCS");
                }
                return 1;
            }

            // Check what to do
            ObjectModel model;
            switch (operation)
            {
                case PluginOperation.List:
                    model = await connection.GetObjectModel();
                    if (model.Plugins.Count > 0)
                    {
                        Console.WriteLine("{0,-24} {1,-16} {2,-16} {3,-24} {4,-24} {5,-12}", "Plugin", "Id", "Version", "Author", "License", "Status");
                        foreach (Plugin item in model.Plugins.Values)
                        {
                            if (item is not null)
                            {
                                string pluginState = "n/a";
                                if (!string.IsNullOrEmpty(item.SbcExecutable))
                                {
                                    pluginState = (item.Pid > 0) ? "Started" : "Stopped";
                                }
                                Console.WriteLine("{0,-24} {1,-16} {2,-16} {3,-24} {4,-24} {5,-12}", item.Name, item.Id, item.Version, item.Author, item.License, pluginState);
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("No plugins installed");
                    }
                    break;

                case PluginOperation.ListData:
                    model = await connection.GetObjectModel();
                    if (model.Plugins.Count > 0)
                    {
                        foreach (Plugin item in model.Plugins.Values)
                        {
                            Console.WriteLine("Plugin {0}:", item.Id);
                            foreach (var kv in item.Data)
                            {
                                Console.WriteLine("{0} = {1}", kv.Key, JsonSerializer.Serialize(kv.Value, DuetAPI.Utility.JsonHelper.DefaultJsonOptions));
                            }
                            Console.WriteLine();
                        }
                    }
                    else
                    {
                        Console.WriteLine("No plugins installed");
                    }
                    break;

                case PluginOperation.Install:
                    try
                    {
                        await connection.InstallPlugin(mainArg!);
                        WriteLine("Plugin installed");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Failed to install plugin: {0}", e.Message);
                    }
                    break;

                case PluginOperation.Reload:
                    try
                    {
                        await connection.ReloadPlugin(mainArg!);
                        WriteLine("Plugin manifest reloaded");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Failed to reload plugin: {0}", e.Message);
                    }
                    break;

                case PluginOperation.Start:
                    try
                    {
                        await connection.StartPlugin(mainArg!);
                        WriteLine("Plugin started");
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine("Failed to start plugin: {0}", e.Message);
                    }
                    break;

                case PluginOperation.SetData:
                    // Parse plugin argument in the format
                    // <plugin>:<key>=<value>
                    string pluginName = string.Empty, key = string.Empty, value = string.Empty;
                    int state = 0;
                    foreach (char c in mainArg!)
                    {
                        switch (state)
                        {
                            case 0:
                                if (c == ':')
                                {
                                    state++;
                                }
                                else
                                {
                                    pluginName += c;
                                }
                                break;
                            case 1:
                                if (c == '=')
                                {
                                    state++;
                                }
                                else
                                {
                                    key += c;
                                }
                                break;
                            case 2:
                                value += c;
                                break;
                        }
                    }

                    // Try to set the data
                    try
                    {
                        try
                        {
                            using JsonDocument json = JsonDocument.Parse(value);
                            await connection.SetPluginData(key, json.RootElement, pluginName);
                        }
                        catch (JsonException)
                        {
                            await connection.SetPluginData(key, value, pluginName);
                        }
                        WriteLine("Plugin data set");
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine("Failed to set plugin data: {0}", e.Message);
                    }
                    break;

                case PluginOperation.Stop:
                    try
                    {
                        await connection.StopPlugin(mainArg!);
                        WriteLine("Plugin stopped");
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine("Failed to stop plugin: {0}", e.Message);
                    }
                    break;

                case PluginOperation.Uninstall:
                    try
                    {
                        await connection.UninstallPlugin(mainArg!);
                        WriteLine("Plugin uninstalled");
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine("Failed to uninstall plugin: {0}", e.Message);
                    }
                    break;

                case PluginOperation.IsInstalled:
                    model = await connection.GetObjectModel();
                    if (model.Plugins.ContainsKey(mainArg!))
                    {
                        WriteLine("Plugin is installed");
                        return 0;
                    }
                    WriteLine("Plugin is not installed");
                    return 1;

                case PluginOperation.IsStarted:
                    model = await connection.GetObjectModel();
                    if (model.Plugins.TryGetValue(mainArg!, out Plugin pluginItem) && pluginItem.Pid > 0)
                    {
                        WriteLine("Plugin is started");
                        return 0;
                    }
                    WriteLine("Plugin is not started");
                    return 1;
            }
            return 0;
        }
    }
}

