using DuetAPI.Connection;
using DuetAPIClient;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace ModelObserver
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Get an optional filter string
            string filter;
            if (args.Length > 0 && args[0] != "-")
            {
                filter = args[0];
            }
            else
            {
                Console.WriteLine("Please enter a filter expression or press RETURN to receive partial model updates:");
                filter = Console.ReadLine().Trim();
            }

            // Connect to DCS
            using SubscribeConnection connection = new SubscribeConnection();
            if (args.Length < 2)
            {
                await connection.Connect(SubscriptionMode.Patch, filter);
            }
            else
            {
                await connection.Connect(SubscriptionMode.Patch, filter, args[1]);
            }
            Console.WriteLine("Connected!");

            // In Patch mode the whole object model is sent over after connecting. Dump it (or call connection.GetMachineModel() to deserialize it)
            _ = await connection.GetSerializedMachineModel();

            // Then keep listening for (filtered) patches
            while (connection.IsConnected)
            {
                using JsonDocument patch = await connection.GetMachineModelPatch();
                if (patch == null)
                {
                    break;
                }
                Console.WriteLine(GetIndentedJson(patch));
            }
        }

        public static string GetIndentedJson(JsonDocument jsonDocument)
        {
            using var stream = new MemoryStream();
            using (Utf8JsonWriter writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                jsonDocument.WriteTo(writer);
            }
            stream.Seek(0, SeekOrigin.Begin);

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
