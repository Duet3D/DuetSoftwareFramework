using DuetAPI;
using DuetAPI.Connection;
using System.CommandLine;
using System.IO;
using CodeLogger;

// General arguments
Option<FileInfo> socketPathOption = new("--socket", "-s")
{
    Description = "UNIX socket to connect to",
    DefaultValueFactory = _ => new FileInfo(Defaults.FullSocketPath)
};

Option<bool> quietOption = new("--quiet", "-q")
{
    Description = "Do not output any messages (not applicable for code replies in interactive mode)",
};

// Main command
Option<InterceptionMode[]> typesOption = new("--type", "-t")
{
    Description = "Interception type(s) (Pre [before processed by DSF], Post [after processed by DSF], or Executed)",
    Arity = ArgumentArity.OneOrMore,
    DefaultValueFactory = _ => [InterceptionMode.Pre],
};

Option<CodeChannel[]?> channelsOption = new("--channel", "-c")
{
    Description = "Input channel(s) where codes may be intercepted. Defaults to all",
    Arity = ArgumentArity.OneOrMore
};

Option<string[]?> filtersOption = new("--filters", "-f")
{
    Description = "Code types that may be intercepted (main codes, keywords, or Q0 for comments)",
};

Option<bool> priorityCodesOption = new("--priority-codes", "-p")
{
    Description = "Intercept priority codes instead of regular codes (not recommended)",
};

RootCommand rootCommand = new("Code logger to intercept G/M/T-codes from DuetControlServer")
{
    socketPathOption,
    quietOption,
    typesOption,
    channelsOption,
    filtersOption,
    priorityCodesOption
};
rootCommand.SetAction((parseResult, token) =>
{
    FileInfo socketPathValue = parseResult.GetRequiredValue(socketPathOption)!;
    bool quietValue = parseResult.GetValue(quietOption);
    InterceptionMode[] typesValue = parseResult.GetRequiredValue(typesOption)!;
    CodeChannel[]? channelsValue = parseResult.GetValue(channelsOption);
    string[]? filtersValue = parseResult.GetValue(filtersOption);
    bool priorityCodesValue = parseResult.GetValue(priorityCodesOption);
    return Commands.MainAsync(socketPathValue, quietValue, typesValue, channelsValue, filtersValue, priorityCodesValue, token);
});

return new CommandLineConfiguration(rootCommand).Invoke(args);
