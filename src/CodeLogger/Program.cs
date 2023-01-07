using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPIClient;
using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace CodeLogger
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            bool quiet = false, priorityCodes = false;
            CodeChannel[]? channels = null;
            string[]? filters = null, types = new string[] { "pre", "post", "executed" };

            // Parse the command line arguments
            string? lastArg = null, socketPath = Defaults.FullSocketPath;
            foreach (string arg in args)
            {
                if (lastArg == "-t" || lastArg == "--types")
                {
                    types = arg.Split(',');
                }
                else if (lastArg == "-c" || lastArg == "--channels")
                {
                    channels = arg.Split(',').Select(item => (CodeChannel)Enum.Parse(typeof(CodeChannel), item, true)).ToArray();
                }
                else if (lastArg == "-f" || lastArg == "--filters")
                {
                    filters = arg.Split(',');
                }
                else if (lastArg == "-s" || lastArg == "--socket")
                {
                    socketPath = arg;
                }
                else if (arg == "-p" || arg == "--priority-codes")
                {
                    priorityCodes = true;
                }
                else if (arg == "-q" || lastArg == "--quiet")
                {
                    quiet = true;
                }
                else if (arg == "-h" || arg == "--help")
                {
                    Console.WriteLine("Available command line arguments:");
                    Console.WriteLine("-t, --types <types>: Comma-delimited interception types (pre, post, or executed)");
                    Console.WriteLine("-c, --channels <channels>: Comma-delimited input channels where codes may be intercepted");
                    Console.WriteLine("-f, --filters <filters>: Comma-delimited code types that may be intercepted (main codes, keywords, or Q0 for comments)");
                    Console.WriteLine("-p, --priority-codes: Intercept priorty codes instead of regular codes (not recommended)");
                    Console.WriteLine("-s, --socket <socket>: UNIX socket to connect to");
                    Console.WriteLine("-q, --quiet: Do not display when a connection has been established");
                    Console.WriteLine("-h, --help: Display this help text");
                    return 0;
                }
                lastArg = arg;
            }

            InterceptConnection? preConnection = null, postConnection = null, executedConnection = null;
            try
            {
                // Connect to DCS
                try
                {
                    if (types.Contains("pre"))
                    {
                        preConnection = new InterceptConnection();
                        await preConnection.Connect(InterceptionMode.Pre, channels, filters, priorityCodes, socketPath);
                    }
                    if (types.Contains("post"))
                    {
                        postConnection = new InterceptConnection();
                        await postConnection.Connect(InterceptionMode.Post, channels, filters, priorityCodes, socketPath);
                    }
                    if (types.Contains("executed"))
                    {
                        executedConnection = new InterceptConnection();
                        await executedConnection.Connect(InterceptionMode.Executed, channels, filters, priorityCodes, socketPath);
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

                // Start waiting for incoming codes
                Task[] tasks = new Task[]
                {
                    (preConnection is not null) ? PrintIncomingCodes(preConnection) : Task.CompletedTask,
                    (postConnection is not null) ? PrintIncomingCodes(postConnection) : Task.CompletedTask,
                    (executedConnection is not null) ? PrintIncomingCodes(executedConnection) : Task.CompletedTask
                };

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

        private static async Task PrintIncomingCodes(InterceptConnection connection)
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

                    Console.WriteLine($"[{connection.Mode}] {code.Channel}: {code}");
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
