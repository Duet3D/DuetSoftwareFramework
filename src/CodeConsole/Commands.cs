using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetAPIClient;

namespace CodeConsole;

/// <summary>
/// Command handlers for the CodeConsole
/// </summary>
public static class Commands
{
    /// <summary>
    /// Main command handler
    /// </summary>
    /// <param name="socketPath">UNIX socket path for IPC</param>
    /// <param name="quiet">Run command quietly</param>
    /// <returns>Exit code</returns>
    public static async Task<int> MainAsync(FileInfo socketPath, bool quiet, CancellationToken cancellationToken)
    {
        // Connect to DCS
        using CommandConnection connection = new();
        try
        {
            await connection.ConnectAsync(socketPath.FullName, cancellationToken);
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
            // Notify the user that a connection has been established
            Console.WriteLine("Connected!");
        }

        // Register an (interactive) user session (optional)
        int sessionId = await connection.AddUserSessionAsync(AccessLevel.ReadWrite, SessionType.Local, "console", cancellationToken);

        // Start reading lines from stdin and send them to DCS as simple codes.
        // When the code has finished, the result is printed to stdout
        string? input = Console.ReadLine();
        while (input is not null and not "exit" and not "quit")
        {
            try
            {
                // startUpdate puts DSF into "updating" mode
                if (input.Equals("startUpdate", StringComparison.InvariantCultureIgnoreCase))
                {
                    await connection.SetUpdateStatusAsync(true, cancellationToken);
                    Console.WriteLine("DSF is now in update mode");
                }

                // endUpdate takes DSF out of "updating" mode
                else if (input.Equals("endUpdate", StringComparison.InvariantCultureIgnoreCase))
                {
                    await connection.SetUpdateStatusAsync(false, cancellationToken);
                    Console.WriteLine("DSF is no longer in update mode");
                }

                // everything else is a code to execute
                else
                {
                    string output = await connection.PerformSimpleCodeAsync(input, DuetAPI.CodeChannel.Telnet, cancellationToken);
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
        try
        {
            if (connection.IsConnected)
            {
                await connection.RemoveUserSessionAsync(sessionId, cancellationToken);
            }
        }
        catch
        {
            // DCS may have shut down (e.g. after a firmware update)
        }
        return 0;
    }

    /// <summary>
    /// Command handler to execute only a single command and exit
    /// </summary>
    /// <param name="code">Code to execute</param>
    /// <param name="socketPath">UNIX socket path for IPC</param>
    /// <param name="quiet">Run command quietly</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Exit code</returns>
    public static async Task<int> ExecAsync(string code, FileInfo socketPath, bool quiet, CancellationToken cancellationToken)
    {
        // Connect to DCS
        using CommandConnection connection = new();
        try
        {
            await connection.ConnectAsync(socketPath.FullName, cancellationToken);
        }
        catch (SocketException)
        {
            if (!quiet)
            {
                Console.Error.WriteLine("Failed to connect to DCS");
            }
            return 1;
        }

        // startUpdate puts DSF into "updating" mode
        if (code.Equals("startUpdate", StringComparison.InvariantCultureIgnoreCase))
        {
            await connection.SetUpdateStatusAsync(true, cancellationToken);
            if (!quiet)
            {
                Console.WriteLine("DSF is now in update mode");
            }
        }

        // endUpdate takes DSF out of "updating" mode
        else if (code.Equals("endUpdate", StringComparison.InvariantCultureIgnoreCase))
        {
            await connection.SetUpdateStatusAsync(false, cancellationToken);
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
        return 0;
    }
}
