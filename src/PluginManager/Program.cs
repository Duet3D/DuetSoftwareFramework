using DuetAPI.Connection;
using PluginManager;
using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;

// Main CLI arguments
var socketPath = new Option<FileInfo>(
    aliases: ["-s", "--socket"],
    description: "UNIX socket to connect to",
    getDefaultValue: () => new FileInfo(Defaults.FullSocketPath)
);

var quiet = new Option<bool>(
    aliases: ["-q", "--quiet"],
    description: "Do not output control messages"
);

var id = new Argument<string>("id", "Plugin ID to reload");

// List command
var listCommand = new Command("list", "List plugin status");
listCommand.SetHandler(Commands.ListAsync, socketPath);

var listDataCommand = new Command("list-data", "List plugin data");
listDataCommand.SetHandler(Commands.ListDataAsync, socketPath);

// Install command
var zipFile = new Argument<FileInfo>("file", "ZIP file to install");
var installCommand = new Command("install", "Install new ZIP bundle")
{
    zipFile
};
installCommand.SetHandler(Commands.InstallAsync, socketPath, quiet, zipFile);

// Reload command
var reloadCommand = new Command("reload", "Reload a plugin manifest")
{
    id
};
reloadCommand.SetHandler(Commands.ReloadAsync, socketPath, quiet, id);

// Start command
var startCommand = new Command("start", "Start a plugin")
{
    id
};
startCommand.SetHandler(Commands.StartAsync, socketPath, quiet, id);

// Set data command
var key = new Argument<string>("key", "Key to set");
var value = new Argument<string>("value", "Value to set");

var setDataCommand = new Command("set-data", "Set plugin data")
{
    id,
    key,
    value
};
setDataCommand.SetHandler(Commands.SetDataAsync, socketPath, quiet, id, key, value);

// Stop command
var stopCommand = new Command("stop", "Stop a plugin")
{
    id
};
stopCommand.SetHandler(Commands.StopAsync, socketPath, quiet, id);

// Uninstall command
var uninstallCommand = new Command("uninstall", "Uninstall a plugin")
{
    id
};
uninstallCommand.SetHandler(Commands.UninstallAsync, socketPath, quiet, id);

// Is installed command
var isInstalledCommand = new Command("is-installed", "Check if a plugin is installed")
{
    id
};
isInstalledCommand.SetHandler(Commands.IsInstalledAsync, socketPath, quiet, id);

// Is started command
var isStartedCommand = new Command("is-started", "Check if a plugin is started")
{
    id
};
isStartedCommand.SetHandler(Commands.IsStartedAsync, socketPath, quiet, id);

// Root command
var rootCommand = new RootCommand("Manage installed third-party DSF plugins")
{
    socketPath,
    quiet,
    listDataCommand,
    installCommand,
    reloadCommand,
    startCommand,
    setDataCommand,
    stopCommand,
    uninstallCommand,
    isInstalledCommand,
    isStartedCommand
};
rootCommand.SetHandler(() =>
{
    Console.Error.WriteLine("No command specified");
    return Task.FromResult(1);
});

await rootCommand.InvokeAsync(args);
