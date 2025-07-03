using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using System.CommandLine;
using System.IO;
using CustomHttpEndpoint;
using System.Runtime.Versioning;

[assembly: UnsupportedOSPlatform("windows")]

// General arguments
Option<FileInfo> socketPathOption = new("--socket", "-s")
{
    Description = "UNIX socket to connect to",
    DefaultValueFactory = _ => new FileInfo(Defaults.FullSocketPath)
};

Option<bool> quietOption = new("--quiet", "-q")
{
    Description = "Do not output any messages",
};

// Main command
Option<HttpEndpointType> methodOption = new("--method", "-m")
{
    Description = "HTTP method to use",
    DefaultValueFactory = _ => HttpEndpointType.GET
};

Option<string> namespaceOption = new("--namespace", "-n")
{
    Description = "Namespace to use",
    DefaultValueFactory = _ => "custom-http-endpoint"
};

Option<string> pathOption = new("--path", "-p")
{
    Description = "HTTP query path",
    DefaultValueFactory = _ => "demo"
};

Option<string> execOption = new("--exec", "-e")
{
    Description = "Command to execute when an HTTP query is received, stdout and stderr are returned as the response body"
};

Option<string> execArgsOption = new("--args", "-a")
{
    Description = "Arguments for the executable command. Query values in % chars are replaced with query options (e.g. %myvalue%). Not applicable for WebSockets"
};

RootCommand rootCommand = new("Create a custom HTTP endpoint in the format /machine/{namespace}/{path}")
{
    socketPathOption,
    quietOption,
    methodOption,
    namespaceOption,
    pathOption,
    execOption,
    execArgsOption
};
rootCommand.SetAction((parserResult, token) => {
    FileInfo socketPathValue = parserResult.GetRequiredValue(socketPathOption);
    bool quietValue = parserResult.GetValue(quietOption);
    HttpEndpointType methodValue = parserResult.GetRequiredValue(methodOption);
    string namespaceValue = parserResult.GetRequiredValue(namespaceOption);
    string pathValue = parserResult.GetRequiredValue(pathOption);
    string? execValue = parserResult.GetValue(execOption);
    string? execArgsValue = parserResult.GetValue(execArgsOption);
    return Commands.MainAsync(socketPathValue, quietValue, methodValue, namespaceValue, pathValue, execValue, execArgsValue, token);
});

return new CommandLineConfiguration(rootCommand).Invoke(args);
