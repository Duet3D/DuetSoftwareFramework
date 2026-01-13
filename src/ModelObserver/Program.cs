using DuetAPI.Connection;
using System.CommandLine;
using System.IO;
using ModelObserver;

// Main CLI arguments
Option<FileInfo> socketPathOption = new("--socket", "-s")
{
    Description = "UNIX socket to connect to",
    DefaultValueFactory = _ => new FileInfo(Defaults.FullSocketPath)
};

Option<bool> quietOption = new("--quiet", "-q")
{
    Description = "Do not output any messages"
};

// Main command
Option<string[]> filter = new("--filter", "-f")
{
    Arity = ArgumentArity.ZeroOrMore,
    Description = "Optional filter expression(s) to apply to the model"
};

Option<bool> confirm = new("--confirm", "-c")
{
    Description = "Confirm every JSON receipt manually"
};

RootCommand rootCommand = new("Observe the object model using optional filter expressions")
{
    socketPathOption,
    quietOption,
    filter,
    confirm
};
rootCommand.SetAction((parserResult, token) => {
    FileInfo socketPathValue = parserResult.GetRequiredValue(socketPathOption);
    bool quietValue = parserResult.GetValue(quietOption);
    string[] filterValue = parserResult.GetValue(filter) ?? [];
    bool confirmValue = parserResult.GetValue(confirm);
    return Commands.MainAsync(socketPathValue, quietValue, filterValue, confirmValue, token);
});

rootCommand.Parse(args).Invoke();
