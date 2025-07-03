using DuetAPI.Connection;
using System.CommandLine;
using System.IO;
using CodeStream;

// General arguments
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
Option<int> bufferSizeOption = new("--buffer-size", "-b")
{
    Description = "Maximum number of codes to buffer at once",
    DefaultValueFactory = _ => 32
};
bufferSizeOption.Validators.Add((parseResult) =>
{
    if (parseResult.GetValue(bufferSizeOption) < 1)
    {
        parseResult.AddError("Buffer size must be greater than or equal to 1");
    }
});

RootCommand rootCommand = new("Code stream to send G/M/T-codes to DuetControlServer")
{
    socketPathOption,
    quietOption,
    bufferSizeOption
};
rootCommand.SetAction((parseResult, token) => {
    FileInfo socketPathValue = parseResult.GetRequiredValue(socketPathOption)!;
    bool quietValue = parseResult.GetValue(quietOption);
    int bufferSizeValue = parseResult.GetRequiredValue(bufferSizeOption);
    return Commands.MainAsync(socketPathValue, quietValue, bufferSizeValue, token);
});

return new CommandLineConfiguration(rootCommand).Invoke(args);
