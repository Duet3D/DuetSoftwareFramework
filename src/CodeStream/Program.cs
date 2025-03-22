using DuetAPI.Connection;
using System.CommandLine;
using System.IO;
using CodeStream;

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
var bufferSize = new Option<int>(
    aliases: ["-b", "--buffer-size"],
    description: "Maximum number of codes to buffer at once"
);

var rootCommand = new RootCommand("Code stream to send G/M/T-codes to DuetControlServer")
{
    socketPath,
    quiet,
    bufferSize
};
rootCommand.SetHandler(Commands.MainAsync, socketPath, quiet, bufferSize);

await rootCommand.InvokeAsync(args);
