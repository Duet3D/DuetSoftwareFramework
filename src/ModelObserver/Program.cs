using DuetAPI.Connection;
using System.CommandLine;
using System.IO;
using ModelObserver;

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

// Main command
var filter = new Option<string[]>(
    aliases: ["-f", "--filter"],
    description: "Filter expression to apply to the model"
);

var confirm = new Option<bool>(
    aliases: ["-c", "--confirm"],
    description: "Confirm every JSON receipt manually"
);

var rootCommand = new RootCommand("Observe the object model using optional filter expressions")
{
    socketPath,
    quiet,
    filter,
    confirm
};
rootCommand.SetHandler((context) => {
    var socketPathValue = context.ParseResult.GetValueForOption(socketPath)!;
    var quietValue = context.ParseResult.GetValueForOption(quiet);
    var filterValue = context.ParseResult.GetValueForOption(filter) ?? [];
    var confirmValue = context.ParseResult.GetValueForOption(confirm);
    return Commands.MainAsync(socketPathValue, quietValue, filterValue, confirmValue, context.GetCancellationToken());
});

await rootCommand.InvokeAsync(args);
