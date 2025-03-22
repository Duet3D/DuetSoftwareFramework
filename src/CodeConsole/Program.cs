using CodeConsole;
using DuetAPI.Connection;
using System.CommandLine;
using System.IO;

// Main CLI arguments
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
var rootCommand = new RootCommand("Code console to send G/M/T-codes to DuetControlServer")
{
    socketPath,
    quiet
};
rootCommand.SetHandler(Commands.MainAsync, socketPath, quiet);

// Exec command
var code = new Argument<string>("code", "The code to execute");
var execCommand = new Command("exec", "Execute the given code(s), wait for the result and exit")
{
    code
};
execCommand.AddAlias("-c");
execCommand.AddAlias("--code");
execCommand.SetHandler(Commands.ExecAsync, socketPath, quiet, code);

rootCommand.AddCommand(execCommand);

return await rootCommand.InvokeAsync(args);
