using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetAPIClient;
using System;
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
            Start,
            SetData,
            Stop,
            Uninstall
        }

        public static async Task Main(string[] args)
        {
            // Parse the command line arguments
            PluginOperation operation = PluginOperation.List;
            string lastArg = null, plugin = null, socketPath = Defaults.FullSocketPath;
            foreach (string arg in args)
            {
                if (lastArg == "-s" || lastArg == "--socket")
                {
                    socketPath = arg;
                }
                else if (lastArg == "install")
                {
                    operation = PluginOperation.Install;
                    plugin = arg;
                }
                else if (lastArg == "start")
                {
                    operation = PluginOperation.Start;
                    plugin = arg;
                }
                else if (lastArg == "set-data")
                {
                    operation = PluginOperation.SetData;
                    plugin = arg;
                }
                else if (lastArg == "stop")
                {
                    operation = PluginOperation.Stop;
                    plugin = arg;
                }
                else if (lastArg == "uninstall")
                {
                    operation = PluginOperation.Uninstall;
                    plugin = arg;
                }
                else if (arg == "list")
                {
                    operation = PluginOperation.List;
                }
                else if (arg == "list-data")
                {
                    operation = PluginOperation.ListData;
                }
                else if (arg == "-h" || arg == "--help")
                {
                    Console.WriteLine("Available command line arguments:");
                    Console.WriteLine("list: List plugin status (default)");
                    Console.WriteLine("list-data: List plugin data");
                    Console.WriteLine("install <zipfile>: Install new ZIP bundle");
                    Console.WriteLine("start <name>: Start a plugin");
                    Console.WriteLine("set-data <plugin>:<key>=<value>: Set plugin data (JSON or text)");
                    Console.WriteLine("stop <name>: Stop a plugin");
                    Console.WriteLine("uninstall <name>: Uninstall a plugin");
                    Console.WriteLine("-s, --socket <socket>: UNIX socket to connect to");
                    Console.WriteLine("-h, --help: Display this help text");
                    return;
                }
                lastArg = arg;
            }

            // Create a new connection and connect to DuetControlServer
            using CommandConnection connection = new CommandConnection();
            await connection.Connect(socketPath);

            // Check what to do
            ObjectModel model;
            switch (operation)
            {
                case PluginOperation.List:
                    model = await connection.GetObjectModel();
                    Console.WriteLine("{0,16} {1,8} {2,12} {3,10}", "Plugin", "Version", "Author", "License", "Status");
                    foreach (Plugin item in model.Plugins)
                    {
                        Console.WriteLine("{0,16} {1,8} {2,12} {3,10}", item.Name, item.Version, item.Author, item.License, (item.PID > 0) ? "started" : "stopped");
                    }
                    break;

                case PluginOperation.ListData:
                    model = await connection.GetObjectModel();
                    foreach (Plugin item in model.Plugins)
                    {
                        Console.WriteLine("Plugin {0}:", item.Name);
                        foreach (var kv in item.Data)
                        {
                            Console.WriteLine("{0} = {1}", kv.Key, JsonSerializer.Serialize(kv.Value, DuetAPI.Utility.JsonHelper.DefaultJsonOptions));
                        }
                        Console.WriteLine();
                    }
                    break;

                case PluginOperation.Install:
                    await connection.InstallPlugin(plugin);
                    break;

                case PluginOperation.Start:
                    await connection.StartPlugin(plugin);
                    break;

                case PluginOperation.SetData:
                    // Parse plugin argument in the format
                    // <plugin>:<key>=<value>
                    string pluginName = string.Empty, key = string.Empty, value = string.Empty;
                    int state = 0;
                    foreach (char c in plugin)
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
                        using JsonDocument json = JsonDocument.Parse(value);
                        await connection.SetPluginData(key, json.RootElement, pluginName);
                    }
                    catch (JsonException)
                    {
                        await connection.SetPluginData(key, value, pluginName);
                    }
                    break;

                case PluginOperation.Stop:
                    await connection.StopPlugin(plugin);
                    break;

                case PluginOperation.Uninstall:
                    await connection.UninstallPlugin(plugin);
                    break;
            }
        }
    }
}

