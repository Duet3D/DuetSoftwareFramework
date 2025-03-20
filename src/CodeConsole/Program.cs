using DuetAPI.Connection;
using DuetAPIClient;
using System;
using System.CommandLine;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;

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
var rootCommand = new RootCommand("Code console to send G/M/T-codes to DuetControlServer")
{
    socketPath,
    quiet
};

rootCommand.SetHandler((socketPath, quiet) =>
{
    // Connect to DCS
    using CommandConnection connection = new();
    try
    {
        connection.Connect(socketPath.FullName);
    }
    catch (SocketException)
    {
        if (!quiet)
        {
            Console.Error.WriteLine("Failed to connect to DCS");
        }
        return Task.FromResult(1);
    }

    if (!quiet)
    {
        // Notify the user that a connection has been established
        Console.WriteLine("Connected!");
    }

    // Register an (interactive) user session (optional)
    int sessionId = connection.AddUserSession(DuetAPI.ObjectModel.AccessLevel.ReadWrite, DuetAPI.ObjectModel.SessionType.Local, "console");

    // Start reading lines from stdin and send them to DCS as simple codes.
    // When the code has finished, the result is printed to stdout
    string? input = Console.ReadLine();
    while (input is not null && !(input is "exit" or "quit"))
    {
        try
        {
            // startUpdate puts DSF into "updating" mode
            if (input.Equals("startUpdate", StringComparison.InvariantCultureIgnoreCase))
            {
                connection.SetUpdateStatus(true);
                Console.WriteLine("DSF is now in update mode");
            }

            // endUpdate takes DSF out of "updating" mode
            else if (input.Equals("endUpdate", StringComparison.InvariantCultureIgnoreCase))
            {
                connection.SetUpdateStatus(false);
                Console.WriteLine("DSF is no longer in update mode");
            }

            // everything else is a code to execute
            else
            {
                string output = connection.PerformSimpleCode(input, DuetAPI.CodeChannel.Telnet);
                if (output.EndsWith(Environment.NewLine))
                {
                    Console.Write(output);
                }
                else
                {
                    Console.WriteLine(output);
                }
            }
        }
        catch (SocketException)
        {
            Console.WriteLine("Server has closed the connection");
            break;
        }
        catch (Exception e)
        {
            if (e is AggregateException ae)
            {
                e = ae.InnerException!;
            }
            Console.WriteLine(e.Message);
        }
        input = Console.ReadLine();
    }

    // Unregister this session again (recommended if there is a registered session)
    if (connection.IsConnected)
    {
        connection.RemoveUserSession(sessionId);
    }
    return Task.FromResult(0);
}, socketPath, quiet);

// exec command
var code = new Argument<string>("code", "The code to execute");
var execCommand = new Command("exec", "Execute the given code(s), wait for the result and exit")
{
    code
};
execCommand.AddAlias("-c");
execCommand.AddAlias("--code");
execCommand.SetHandler((socketPath, code, quiet) =>
{
    // Connect to DCS
    using CommandConnection connection = new();
    try
    {
        connection.Connect(socketPath.FullName);
    }
    catch (SocketException)
    {
        if (!quiet)
        {
            Console.Error.WriteLine("Failed to connect to DCS");
        }
        return Task.FromResult(1);
    }

    // startUpdate puts DSF into "updating" mode
    if (code.Equals("startUpdate", StringComparison.InvariantCultureIgnoreCase))
    {
        connection.SetUpdateStatus(true);
        if (!quiet)
        {
            Console.WriteLine("DSF is now in update mode");
        }
    }

    // endUpdate takes DSF out of "updating" mode
    else if (code.Equals("endUpdate", StringComparison.InvariantCultureIgnoreCase))
    {
        connection.SetUpdateStatus(false);
        if (!quiet)
        {
            Console.WriteLine("DSF is no longer in update mode");
        }
    }

    // everything else is a code to execute
    else
    {
        string output = connection.PerformSimpleCode(code);
        if (!quiet)
        {
            if (output.EndsWith('\n'))
            {
                Console.Write(output);
            }
            else
            {
                Console.WriteLine(output);
            }
        }
    }
    return Task.FromResult(0);
}, socketPath, code, quiet);
rootCommand.AddCommand(execCommand);

return await rootCommand.InvokeAsync(args);
