using System;
using System.IO;
using System.Linq;
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
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Exit code</returns>
    public static async Task<int> MainAsync(FileInfo socketPath, bool quiet, InterceptionMode[] types, CodeChannel[]? channels, string[]? filters, bool priorityCodes, CancellationToken cancellationToken)
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
                    await preConnection.ConnectAsync(InterceptionMode.Pre, channels, filters, priorityCodes, socketPath.FullName, cancellationToken);
                }
                if (types.Contains(InterceptionMode.Post))
                {
                    postConnection = new InterceptConnection();
                    await postConnection.ConnectAsync(InterceptionMode.Post, channels, filters, priorityCodes, socketPath.FullName, cancellationToken);
                }
                if (types.Contains(InterceptionMode.Executed))
                {
                    executedConnection = new InterceptConnection();
                    await executedConnection.ConnectAsync(InterceptionMode.Executed, channels, filters, priorityCodes, socketPath.FullName, cancellationToken);
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

            // Keep listening on those connections
            Task[] tasks =
            [
                (preConnection is not null) ? PrintIncomingCodesAsync(preConnection, quiet, cancellationToken) : Task.CompletedTask,
                (postConnection is not null) ? PrintIncomingCodesAsync(postConnection, quiet, cancellationToken) : Task.CompletedTask,
                (executedConnection is not null) ? PrintIncomingCodesAsync(executedConnection, quiet, cancellationToken) : Task.CompletedTask
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
