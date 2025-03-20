using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPIClient;
using System;
using System.Collections.Generic;
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
var types = new Option<List<InterceptionMode>>(
    aliases: ["-t", "--types"],
    description: "Interception types (pre [before processed by DSF], post [after processed by DSF], or executed)",
    getDefaultValue: () => [ InterceptionMode.Pre ]
);

var channels = new Option<List<CodeChannel>?>(
    aliases: ["-c", "--channels"],
    description: "Input channels where codes may be intercepted. Defaults to all"
);

var filters = new Option<List<string>?>(
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

rootCommand.SetHandler(async (socketPath, quiet, types, channels, filters, priorityCodes) =>
{
    InterceptConnection? preConnection = null, postConnection = null, executedConnection = null;
    try
    {
        // Connect to DCS
        try
        {
            if (types.Contains(InterceptionMode.Pre))
            {
                preConnection = new InterceptConnection();
                preConnection.Connect(InterceptionMode.Pre, channels, filters, priorityCodes, socketPath.FullName);
            }
            if (types.Contains(InterceptionMode.Post))
            {
                postConnection = new InterceptConnection();
                postConnection.Connect(InterceptionMode.Post, channels, filters, priorityCodes, socketPath.FullName);
            }
            if (types.Contains(InterceptionMode.Executed))
            {
                executedConnection = new InterceptConnection();
                executedConnection.Connect(InterceptionMode.Executed, channels, filters, priorityCodes, socketPath.FullName);
            }
        }
        catch (SocketException)
        {
            if (!quiet)
            {
                Console.Error.WriteLine("Failed to connect to DCS");
            }
            return;
        }

        if (!quiet)
        {
            Console.WriteLine("Connected!");
        }

        // Keep listening on those connections
        async Task PrintIncomingCodes(InterceptConnection connection)
        {
            try
            {
                Code code;
                do
                {
                    code = await connection.ReceiveCodeAsync();

                    Console.WriteLine($"[{connection.Mode}] {code.Channel}: {code}");

                    // If you do not wish to let the received code execute, you can run connection.ResolveCode instead.
                    // Before you call one of Cancel, Ignore, or Resolve you may execute as many commands as you want.
                    // Codes initiated from the intercepting connection cannot be intercepted from the same connection.
                    await connection.IgnoreCodeAsync();
                }
                while (true);
            }
            catch (SocketException)
            {
                // Server has closed the connection
            }
        }

        Task[] tasks =
        [
            (preConnection is not null) ? PrintIncomingCodes(preConnection) : Task.CompletedTask,
            (postConnection is not null) ? PrintIncomingCodes(postConnection) : Task.CompletedTask,
            (executedConnection is not null) ? PrintIncomingCodes(executedConnection) : Task.CompletedTask
        ];

        // Wait for all tasks to finish
        await Task.WhenAll(tasks);
    }
    finally
    {
        preConnection?.Dispose();
        postConnection?.Dispose();
        executedConnection?.Dispose();
    }
}, socketPath, quiet, types, channels, filters, priorityCodes);
