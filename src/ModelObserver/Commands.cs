using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Connection;
using DuetAPIClient;

namespace ModelObserver;

public static class Commands
{
    /// <summary>
    /// Main command handler
    /// </summary>
    /// <param name="socketPath">UNIX socket path</param>
    /// <param name="quiet">Disable output messages</param>
    /// <param name="filters">Object model filters</param>
    /// <param name="confirm">Confirm object model receipts</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns></returns>
    public static async Task<int> MainAsync(FileInfo socketPath, bool quiet, string[] filters, bool confirm, CancellationToken cancellationToken)
    {
        // Get an optional filter string
        if (!quiet && filters.Length == 0)
        {
            Console.WriteLine("Please enter a filter expression or press RETURN to receive partial model updates:");
            string? line = Console.ReadLine();
            if (line is not null)
            {
                filters = [.. line.Trim().Split(',', '|').Select(filter => filter.Trim())];
                if (filters is null)
                {
                    Console.Error.WriteLine("Invalid filter string!");
                    return 1;
                }
            }
        }
        else if (filters.Length == 1 && filters[0] == "null")
        {
            filters = [];
        }

        // Connect to DCS
        using SubscribeConnection connection = new();
        try
        {
            await connection.ConnectAsync(SubscriptionMode.Patch, filters, socketPath.FullName);
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

        // Write incoming fragments indented to the console
        do
        {
            try
            {
                if (confirm)
                {
                    Console.ReadLine();
                }
                using JsonDocument patch = await connection.GetObjectModelPatchAsync(cancellationToken);
                Console.WriteLine(GetIndentedJson(patch));
            }
            catch (OperationCanceledException)
            {
                // expected on termination
            }
            catch (SocketException)
            {
                if (!quiet)
                {
                    Console.WriteLine("Server has closed the connection");
                }
                break;
            }
        }
        while (!cancellationToken.IsCancellationRequested);

        // End
        return 0;
    }

    private static string GetIndentedJson(JsonDocument jsonDocument)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            jsonDocument.WriteTo(writer);
        }
        stream.Seek(0, SeekOrigin.Begin);

        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
