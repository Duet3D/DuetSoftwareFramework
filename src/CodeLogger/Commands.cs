using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPIClient;

namespace CodeLogger;

/// <summary>
/// Commands for the CodeLogger
/// </summary>
public static class Commands
{
    /// <summary>
    /// Main command handler
    /// </summary>
    /// <param name="socketPath">UNIX socket path for IPC</param>
    /// <param name="quiet">Run command quietly</param>
    /// <param name="types">Interception types</param>
    /// <param name="channels">Channels to intercept</param>
    /// <param name="filters">Code filters</param>
    /// <param name="priorityCodes">Intercept exit codes</param>
    /// <returns>Exit code</returns>
    public static async Task<int> MainAsync(FileInfo socketPath, bool quiet, List<InterceptionMode> types, List<CodeChannel>? channels, List<string>? filters, bool priorityCodes)
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
                    await preConnection.ConnectAsync(InterceptionMode.Pre, channels, filters, priorityCodes, socketPath.FullName);
                }
                if (types.Contains(InterceptionMode.Post))
                {
                    postConnection = new InterceptConnection();
                    await postConnection.ConnectAsync(InterceptionMode.Post, channels, filters, priorityCodes, socketPath.FullName);
                }
                if (types.Contains(InterceptionMode.Executed))
                {
                    executedConnection = new InterceptConnection();
                    await executedConnection.ConnectAsync(InterceptionMode.Executed, channels, filters, priorityCodes, socketPath.FullName);
                }
            }
            catch (SocketException)
            {
                if (!quiet)
                {
                    Console.Error.WriteLine("Failed to connect to DCS");
                }
                return 1;
            }

            if (!quiet)
            {
                Console.WriteLine("Connected!");
            }

            // Catch Ctrl+C and stop the tasks when requested
            using CancellationTokenSource cts = new();
            Console.CancelKeyPress += (sender, args) =>
            {
                if (!cts.IsCancellationRequested)
                {
                    cts.Cancel();
                    args.Cancel = true;
                }
            };

            // Keep listening on those connections
            Task[] tasks =
            [
                (preConnection is not null) ? PrintIncomingCodesAsync(preConnection, quiet, cts.Token) : Task.CompletedTask,
                (postConnection is not null) ? PrintIncomingCodesAsync(postConnection, quiet, cts.Token) : Task.CompletedTask,
                (executedConnection is not null) ? PrintIncomingCodesAsync(executedConnection, quiet, cts.Token) : Task.CompletedTask
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
        return 0;
    }

    private static async Task PrintIncomingCodesAsync(InterceptConnection connection, bool quiet, CancellationToken cancellationToken)
    {
        try
        {
            Code code;
            do
            {
                // Receive the next code from DCS
                code = await connection.ReceiveCodeAsync(cancellationToken);

                // Print the received code to stdout
                Console.WriteLine($"[{connection.Mode}] {code.Channel}: {code}");

                // If you do not wish to let the received code execute, you can run connection.ResolveCode instead.
                // Before you call one of Cancel, Ignore, or Resolve you may execute as many commands as you want.
                // Codes initiated from the intercepting connection cannot be intercepted from the same connection.
                // DSF 3.6 and newer also let you rewrite (replace) the code before it is executed.
                await connection.IgnoreCodeAsync(cancellationToken);
            }
            while (!cancellationToken.IsCancellationRequested);
        }
        catch (SocketException)
        {
            if (!quiet)
            {
                Console.Error.WriteLine("Server has closed the connection.");
            }
        }
    }

}
