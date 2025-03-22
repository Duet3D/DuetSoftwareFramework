using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DuetAPIClient;

namespace CodeStream;

/// <summary>
/// Command handlers for the CodeStream
/// </summary>
public static class Commands
{
    /// <summary>
    /// Main command handler
    /// </summary>
    /// <param name="socketPath">UNIX socket path for IPC</param>
    /// <param name="quiet">Run command quietly</param>
    /// <param name="bufferSize">Maximum number of commands to buffer</param>
    /// <returns></returns>
    public static async Task<int> MainAsync(FileInfo socketPath, bool quiet, int bufferSize)
    {
        // Create a new connection and connect to DuetControlServer
        using CodeStreamConnection connection = new();
        try
        {
            await connection.ConnectAsync(bufferSize, DuetAPI.CodeChannel.Telnet, socketPath.FullName);
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

        // Start streaming
        using CancellationTokenSource cts = new();
        await using NetworkStream stream = connection.GetStream();
        Task inputTask = Task.Run(async () => await ReadCodesAsync(stream, cts));   // This is started with Task.Run() because Console.ReadLine blocks...
        Task outputTask = WriteRepliesAsync(stream, quiet, cts.Token);
        await Task.WhenAll(inputTask, outputTask);

        // User or server closed the connection
        return 0;
    }

    private static async Task ReadCodesAsync(Stream socketStream, CancellationTokenSource cancellationTokenSource)
    {
        await using StreamWriter writer = new(socketStream);
        do
        {
            try
            {
                // Read the next line from stdin
                string? line = Console.ReadLine();
                if (line is null || line == "exit" || line == "quit")
                {
                    cancellationTokenSource.Cancel();
                    break;
                }

                // Send it to DCS
                await writer.WriteLineAsync(line);
                await writer.FlushAsync();
            }
            catch (SocketException)
            {
                // User must have pressed Ctrl+C
                break;
            }
        }
        while (!cancellationTokenSource.IsCancellationRequested);
    }

    private static async Task WriteRepliesAsync(Stream socketStream, bool quiet, CancellationToken cancellationToken)
    {
        using StreamReader reader = new(socketStream);
        do
        {
            try
            {
                // Read the next line from DCS
                string? line = await reader.ReadLineAsync(cancellationToken);

                // Write it to stdout
                Console.WriteLine(line);
            }
            catch (SocketException)
            {
                if (!quiet)
                {
                    Console.WriteLine("Server has closed the connection");
                    break;
                }
            }
        }
        while (!cancellationToken.IsCancellationRequested);
    }
}
