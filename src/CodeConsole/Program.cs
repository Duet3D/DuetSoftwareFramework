using CodeConsole;
using DuetAPI.Connection;
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
    Description = "Do not output any messages (not applicable for code replies in interactive mode)"
};

// Main command
RootCommand rootCommand = new("Code console to send G/M/T-codes to DuetControlServer")
{
    socketPathOption,
    quietOption
};
rootCommand.SetAction((parseResult, token) => {
    FileInfo socketPathValue = parseResult.GetRequiredValue(socketPathOption);
    bool quietValue = parseResult.GetValue(quietOption);
    return Commands.MainAsync(socketPathValue, quietValue, token);
});

// Exec command
Argument<string> codeArgument = new("code")
{
    Description = "Code to execute"
};
Command execCommand = new("exec", "Executes the given code(s) and waits for the result before exiting.")
{
    quietOption,
    socketPathOption,
    codeArgument
};
execCommand.Aliases.Add("-c");
execCommand.Aliases.Add("--code");
execCommand.SetAction((parseResult, token) =>
{
    string codeValue = parseResult.GetRequiredValue(codeArgument);
    FileInfo socketPathValue = parseResult.GetRequiredValue(socketPathOption);
    bool quietValue = parseResult.GetValue(quietOption);
    return Commands.ExecAsync(codeValue, socketPathValue, quietValue, token);
});

rootCommand.Subcommands.Add(execCommand);
return rootCommand.Parse(args).Invoke();
