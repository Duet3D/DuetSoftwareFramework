using DuetAPI.Connection;
using DuetAPIClient;
using System;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

namespace ModelObserver
{
    public static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            // Parse the command line arguments
            string? lastArg = null, socketPath = Defaults.FullSocketPath, filter = null;
            bool confirm = false, quiet = false;
            foreach (string arg in args)
            {
                switch (lastArg)
                {
                    case "-s" or "--socket":
                        socketPath = arg;
                        break;
                    case "-f" or "--filter":
                        filter = arg;
                        break;
                    default:
                        if (arg is "-c" or "--confirm")
                        {
                            confirm = true;
                        }
                        else if (arg is "-q" or "--quiet")
                        {
                            quiet = true;
                        }
                        else if (arg is "-h" or "--help")
                        {
                            Console.WriteLine("Available command line arguments:");
                            Console.WriteLine("-s, --socket <socket>: UNIX socket to connect to");
                            Console.WriteLine("-f, --filter <filter>: UNIX socket to connect to");
                            Console.WriteLine("-c, --confirm: Confirm every JSON receipt manually");
                            Console.WriteLine("-q, --quiet: Do not display when a connection has been established");
                            Console.WriteLine("-h, --help: Display this help text");
                            return 0;
                        }

                        break;
                }
                lastArg = arg;
            }

            // Get an optional filter string
            if (string.IsNullOrWhiteSpace(filter))
            {
                Console.WriteLine("Please enter a filter expression or press RETURN to receive partial model updates:");
                filter = Console.ReadLine()?.Trim();
                if (filter is null)
                {
                    Console.Error.WriteLine("Invalid filter string!");
                    return 1;
                }
            }

            // Connect to DCS
            using SubscribeConnection connection = new();
            try
            {
#pragma warning disable CS0618 // Type or member is obsolete
                await connection.Connect(SubscriptionMode.Patch, filter, socketPath);
#pragma warning restore CS0618 // Type or member is obsolete
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
                    using JsonDocument patch = await connection.GetObjectModelPatch();
                    Console.WriteLine(GetIndentedJson(patch));
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
            while (true);
            return 0;
        }

        /// <summary>
        /// Print a JSON document as indented JSON text
        /// </summary>
        /// <param name="jsonDocument"></param>
        /// <returns>Indented text</returns>
        public static string GetIndentedJson(JsonDocument jsonDocument)
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
}
