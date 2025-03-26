using DuetAPI;
using DuetAPI.Connection;
using System.CommandLine;
using System.IO;
using CodeLogger;

// General arguments
var socketPath = new Option<FileInfo>(
    aliases: ["-s", "--socket"],
    description: "UNIX socket to connect to",
    getDefaultValue: () => new FileInfo(Defaults.FullSocketPath)
);

var quiet = new Option<bool>(
    aliases: ["-q", "--quiet"],
    description: "Do not output any messages (not applicable for code replies in interactive mode)"
);

// Main command
var types = new Option<InterceptionMode[]>(
    aliases: ["-t", "--types"],
    description: "Interception types (pre [before processed by DSF], post [after processed by DSF], or executed)",
    getDefaultValue: () => [ InterceptionMode.Pre ]
);

var channels = new Option<CodeChannel[]?>(
    aliases: ["-c", "--channels"],
    description: "Input channels where codes may be intercepted. Defaults to all"
);

var filters = new Option<string[]?>(
    aliases: ["-f", "--filters"],
    description: "Code types that may be intercepted (main codes, keywords, or Q0 for comments)"
);

var priorityCodes = new Option<bool>(
    aliases: ["-p", "--priority-codes"],
    description: "Intercept priorty codes instead of regular codes (not recommended)"
);

var rootCommand = new RootCommand("Code logger to intercept G/M/T-codes from DuetControlServer")
{
    socketPath,
    quiet,
    types,
    channels,
    filters,
    priorityCodes
};
rootCommand.SetHandler((context) =>
{
    var socketPathValue = context.ParseResult.GetValueForOption(socketPath)!;
    var quietValue = context.ParseResult.GetValueForOption(quiet);
    var typesValue = context.ParseResult.GetValueForOption(types)!;
    var channelsValue = context.ParseResult.GetValueForOption(channels);
    var filtersValue = context.ParseResult.GetValueForOption(filters);
    var priorityCodesValue = context.ParseResult.GetValueForOption(priorityCodes);
    return Commands.MainAsync(socketPathValue, quietValue, typesValue, channelsValue, filtersValue, priorityCodesValue, context.GetCancellationToken());
});

return await rootCommand.InvokeAsync(args);
