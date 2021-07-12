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
        private static async Task Main(string[] args)
        {
            // Parse the command line arguments
            string lastArg = null, socketPath = Defaults.FullSocketPath, filter = null;
            bool quiet = false;
            foreach (string arg in args)
            {
                if (lastArg == "-s" || lastArg == "--socket")
                {
                    socketPath = arg;
                }
                else if (lastArg == "-f" || lastArg == "--filter")
                {
                    filter = arg;
                }
                else if (arg == "-q" || arg == "--quiet")
                {
                    quiet = true;
                }
                else if (arg == "-h" || arg == "--help")
                {
                    Console.WriteLine("Available command line arguments:");
                    Console.WriteLine("-s, --socket <socket>: UNIX socket to connect to");
                    Console.WriteLine("-f, --filter <filter>: UNIX socket to connect to");
                    Console.WriteLine("-q, --quiet: Do not display when a connection has been established");
                    Console.WriteLine("-h, --help: Display this help text");
                    return;
                }
                lastArg = arg;
            }

            // Get an optional filter string
            if (string.IsNullOrWhiteSpace(filter))
            {
                Console.WriteLine("Please enter a filter expression or press RETURN to receive partial model updates:");
                filter = Console.ReadLine().Trim();
            }

            // Connect to DCS
            using SubscribeConnection connection = new();
#pragma warning disable CS0618 // Type or member is obsolete
            await connection.Connect(SubscriptionMode.Patch, filter, socketPath);
#pragma warning restore CS0618 // Type or member is obsolete

            if (!quiet)
            {
                Console.WriteLine("Connected!");
            }

            // Write incoming fragments indented to the console
            do
            {
                try
                {
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
