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
                    if (model.Plugins.Count > 0)
                    {
                        Console.WriteLine("{0,-24} {1,-16} {2,-24} {3,-12} {4,-12}", "Plugin", "Version", "Author", "License", "Status");
                        foreach (Plugin item in model.Plugins)
                        {
                            string pluginState = "n/a";
                            if (!string.IsNullOrEmpty(item.SbcExecutable))
                            {
                                pluginState = (item.Pid > 0) ? "Started" : "Stopped";
                            }
                            Console.WriteLine("{0,-24} {1,-16} {2,-24} {3,-12} {4,-12}", item.Name, item.Version, item.Author, item.License, pluginState);
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
                        foreach (Plugin item in model.Plugins)
                        {
                            Console.WriteLine("Plugin {0}:", item.Name);
                            foreach (var kv in item.SbcData)
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
                        await connection.InstallPlugin(plugin);
                        Console.WriteLine("Plugin installed");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Failed to install plugin: {0}", e.Message);
                    }
                    break;

                case PluginOperation.Start:
                    try
                    {
                        await connection.StartPlugin(plugin);
                        Console.WriteLine("Plugin started");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Failed to start plugin: {0}", e.Message);
                    }
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
                        try
                        {
                            using JsonDocument json = JsonDocument.Parse(value);
                            await connection.SetPluginData(key, json.RootElement, pluginName);
                        }
                        catch (JsonException)
                        {
                            await connection.SetPluginData(key, value, pluginName);
                        }
                        Console.WriteLine("Plugin data set");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Failed to set plugin data: {0}", e.Message);
                    }
                    break;

                case PluginOperation.Stop:
                    try
                    {
                        await connection.StopPlugin(plugin);
                        Console.WriteLine("Plugin stopped");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Failed to stop plugin: {0}", e.Message);
                    }
                    break;

                case PluginOperation.Uninstall:
                    try
                    {
                        await connection.UninstallPlugin(plugin);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Failed to uninstall plugin: {0}", e.Message);
                    }
                    break;
            }
        }
    }
}

