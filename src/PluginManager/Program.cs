using DuetAPI.Connection;
using PluginManager;
using System.CommandLine;
using System.IO;

// Main CLI arguments
Option<FileInfo> socketPathOption = new("--socket", "-s")
{
    Description = "UNIX socket to connect to",
    DefaultValueFactory = _ => new FileInfo(Defaults.FullSocketPath)
};

Option<bool> quietOption = new("--quiet", "-q")
{
    Description = "Do not output control messages"
};

Argument<string> idArgument = new("id")
{
    Description = "Plugin identifier"
};

// List command
Command listCommand = new("list", "List plugin status")
{
    socketPathOption
};
listCommand.SetAction((parserResult, token) =>
{
    FileInfo socketPathValue = parserResult.GetRequiredValue(socketPathOption);
    return Commands.ListAsync(socketPathValue, token);
});

// List data command
Command listDataCommand = new("list-data", "List plugin data")
{
    socketPathOption
};
listDataCommand.SetAction((parserResult, token) => {
    FileInfo socketPathValue = parserResult.GetRequiredValue(socketPathOption);
    return Commands.ListDataAsync(socketPathValue, token);
});

// Install command
Argument<FileInfo> zipFileOption = new("file")
{
    Description = "ZIP file to install"
};
Command installCommand = new("install", "Install new ZIP bundle")
{
    socketPathOption,
    quietOption,
    zipFileOption
};
installCommand.SetAction((parserResult, token) =>
{
    FileInfo socketPathValue = parserResult.GetRequiredValue(socketPathOption);
    bool quietValue = parserResult.GetValue(quietOption);
    FileInfo zipFileValue = parserResult.GetRequiredValue(zipFileOption);
    return Commands.InstallAsync(socketPathValue, quietValue, zipFileValue, token);
});

// Reload command
Command reloadCommand = new("reload", "Reload a plugin manifest")
{
    idArgument
};
reloadCommand.SetAction((parserResult, token) =>
{
    FileInfo socketPathValue = parserResult.GetRequiredValue(socketPathOption);
    bool quietValue = parserResult.GetValue(quietOption);
    string idValue = parserResult.GetRequiredValue(idArgument);
    return Commands.ReloadAsync(socketPathValue, quietValue, idValue, token);
});

// Start command
var startCommand = new Command("start", "Start a plugin")
{
    idArgument
};
startCommand.SetAction((parserResult, token) =>
{
    FileInfo socketPathValue = parserResult.GetRequiredValue(socketPathOption);
    bool quietValue = parserResult.GetValue(quietOption);
    string idValue = parserResult.GetRequiredValue(idArgument);
    return Commands.StartAsync(socketPathValue, quietValue, idValue, token);
});

// Set data command
Argument<string> keyArgument = new("key")
{
    Description = "Key to set"
};
Argument<string> valueArgument = new("value")
{
    Description = "Value to set"
};

var setDataCommand = new Command("set-data", "Set plugin data")
{
    socketPathOption,
    quietOption,
    idArgument,
    keyArgument,
    valueArgument
};
setDataCommand.SetAction((parserResult, token) =>
{
    FileInfo socketPathValue = parserResult.GetRequiredValue(socketPathOption);
    bool quietValue = parserResult.GetValue(quietOption);
    string idValue = parserResult.GetRequiredValue(idArgument);
    string keyValue = parserResult.GetRequiredValue(keyArgument);
    string valueValue = parserResult.GetRequiredValue(valueArgument);
    return Commands.SetDataAsync(socketPathValue, quietValue, idValue, keyValue, valueValue, token);
});

// Stop command
var stopCommand = new Command("stop", "Stop a plugin")
{
    socketPathOption,
    quietOption,
    idArgument
};
stopCommand.SetAction((parserResult, token) =>
{
    FileInfo socketPathValue = parserResult.GetRequiredValue(socketPathOption);
    bool quietValue = parserResult.GetValue(quietOption);
    string idValue = parserResult.GetRequiredValue(idArgument);
    return Commands.StopAsync(socketPathValue, quietValue, idValue, token);
});

// Uninstall command
var uninstallCommand = new Command("uninstall", "Uninstall a plugin")
{
    socketPathOption,
    quietOption,
    idArgument
};
uninstallCommand.SetAction((parserResult, token) =>
{
    FileInfo socketPathValue = parserResult.GetRequiredValue(socketPathOption);
    bool quietValue = parserResult.GetValue(quietOption);
    string idValue = parserResult.GetRequiredValue(idArgument);
    return Commands.UninstallAsync(socketPathValue, quietValue, idValue, token);
});

// Is installed command
var isInstalledCommand = new Command("is-installed", "Check if a plugin is installed")
{
    socketPathOption,
    quietOption,
    idArgument
};
isInstalledCommand.SetAction((parseResult, token) =>
{
    FileInfo socketPathValue = parseResult.GetRequiredValue(socketPathOption);
    bool quietValue = parseResult.GetValue(quietOption);
    string idValue = parseResult.GetRequiredValue(idArgument);
    return Commands.IsInstalledAsync(socketPathValue, quietValue, idValue, token);
});

// Is started command
var isStartedCommand = new Command("is-started", "Check if a plugin is started")
{
    socketPathOption,
    quietOption,
    idArgument
};
isStartedCommand.SetAction((parseResult, token) =>
{
    FileInfo socketPathValue = parseResult.GetRequiredValue(socketPathOption);
    bool quietValue = parseResult.GetValue(quietOption);
    string idValue = parseResult.GetRequiredValue(idArgument);
    return Commands.IsStartedAsync(socketPathValue, quietValue, idValue, token);
});

// Root command
var rootCommand = new RootCommand("Manage installed third-party DSF plugins")
{
    socketPathOption,
    quietOption,
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
rootCommand.SetAction((parseResult) =>
{
    parseResult.RootCommandResult.AddError("No command specified");
    return 1;
});

return new CommandLineConfiguration(rootCommand).Invoke(args);
