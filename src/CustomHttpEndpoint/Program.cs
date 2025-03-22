using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using System.CommandLine;
using System.IO;
using CustomHttpEndpoint;

// General arguments
var socketPath = new Option<FileInfo>(
    aliases: ["-s", "--socket"],
    description: "UNIX socket to connect to",
    getDefaultValue: () => new FileInfo(Defaults.FullSocketPath)
);

var quiet = new Option<bool>(
    aliases: ["-q", "--quiet"],
    description: "Do not output any messages"
);

// Main command
var method = new Option<HttpEndpointType>(
    aliases: ["-m", "--method"],
    description: "[GET, POST, PUT, PATCH, TRACE, DELETE, OPTIONS, WebSocket]: HTTP method to use",
    getDefaultValue: () => HttpEndpointType.GET
);

var ns = new Option<string>(
    aliases: ["-n", "--namespace"],
    description: "Namespace to use",
    getDefaultValue: () => "custom-http-endpoint"
);

var path = new Option<string>(
    aliases: ["-p", "--path"],
    description: "HTTP query path",
    getDefaultValue: () => "demo"
);

var cmd = new Option<string>(
    aliases: ["-e", "--exec"],
    description: "Command to execute when an HTTP query is received, stdout and stderr are returned as the response body"
);

var cmdArgs = new Option<string>(
    aliases: ["-a", "--args"],
    description: "Arguments for the executable command. Query values in % chars are replaced with query options (e.g. %myvalue%). Not applicable for WebSockets"
);

var rootCommand = new RootCommand("Create a custom HTTP endpoint in the format /machine/{namespace}/{path}")
{
    socketPath,
    quiet,
    method,
    ns,
    path,
    cmd,
    cmdArgs
};
rootCommand.SetHandler(Commands.MainAsync, socketPath, quiet, method, ns, path, cmd, cmdArgs);

return await rootCommand.InvokeAsync(args);
