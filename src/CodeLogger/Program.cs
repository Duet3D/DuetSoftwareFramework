using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPIClient;
using System;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace CodeLogger
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            // Parse the command line arguments
            string lastArg = null, socketPath = Defaults.FullSocketPath;
            bool quiet = false;
            foreach (string arg in args)
            {
                if (lastArg == "-s" || lastArg == "--socket")
                {
                    socketPath = arg;
                }
                else if (arg == "-q" || lastArg == "--quiet")
                {
                    quiet = true;
                }
                else if (arg == "-h" || arg == "--help")
                {
                    Console.WriteLine("Available command line arguments:");
                    Console.WriteLine("-s, --socket <socket>: UNIX socket to connect to");
                    Console.WriteLine("-q, --quiet: Do not display when a connection has been established");
                    Console.WriteLine("-h, --help: Display this help text");
                    return;
                }
                lastArg = arg;
            }

            // Connect to DCS
            using InterceptConnection preConnection = new InterceptConnection();
            using InterceptConnection postConnection = new InterceptConnection();
            using InterceptConnection executedConnection = new InterceptConnection();

            await preConnection.Connect(InterceptionMode.Pre, socketPath);
            await postConnection.Connect(InterceptionMode.Post, socketPath);
            await executedConnection.Connect(InterceptionMode.Executed, socketPath);

            if (!quiet)
            {
                Console.WriteLine("Connected!");
            }

            // Start waiting for incoming codes
            Task[] tasks = new Task[]
            {
                PrintIncomingCodes("pre", preConnection),
                PrintIncomingCodes("post", postConnection),
                PrintIncomingCodes("executed", executedConnection)
            };

            // Wait for all tasks to finish
            await Task.WhenAll(tasks);
        }

        private static async Task PrintIncomingCodes(string prefix, InterceptConnection connection)
        {
            try
            {
                Code code;
                do
                {
                    code = await connection.ReceiveCode();

                    // If you do not wish to let the received code execute, you can run connection.ResolveCode instead.
                    // Before you call one of Cancel, Ignore, or Resolve you may execute as many commands as you want.
                    // Codes initiated from the intercepting connection cannot be intercepted from the same connection.
                    await connection.IgnoreCode();

                    Console.WriteLine($"[{prefix}] {code.Channel}: {code}");
                }
                while (true);
            }
            catch (SocketException)
            {
                // Server has closed the connection
            }
        }
    }
}
