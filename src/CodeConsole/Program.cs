using DuetAPIClient;
using System;
using System.Threading.Tasks;

namespace CodeConsole
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Connect to DCS
            using CommandConnection connection = new CommandConnection();
            if (args.Length == 0)
            {
                await connection.Connect();
            }
            else
            {
                await connection.Connect(args[0]);
            }
            Console.WriteLine("Connected!");

            // Start reading lines from stdin and send them to DCS as simple codes.
            // When the code has finished, the result is printed to stdout
            string input = Console.ReadLine();
            while (connection.IsConnected && input != null && input != "exit" && input != "quit")
            {
                try
                {
                    string output = connection.PerformSimpleCode(input).Result;
                    Console.Write(output);
                }
                catch (AggregateException ae)
                {
                    Console.WriteLine(ae.InnerException.Message);
                }
                input = Console.ReadLine();
            }
        }
    }
}
