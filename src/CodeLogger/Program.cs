using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPIClient;
using System;
using System.Threading.Tasks;

namespace CodeLogger
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Connect to DCS
            using InterceptConnection preConnection = new InterceptConnection();
            using InterceptConnection postConnection = new InterceptConnection();
            using InterceptConnection executedConnection = new InterceptConnection();
            if (args.Length == 0)
            {
                await preConnection.Connect(InterceptionMode.Pre);
                await postConnection.Connect(InterceptionMode.Post);
                await executedConnection.Connect(InterceptionMode.Executed);
            }
            else
            {
                await preConnection.Connect(InterceptionMode.Pre, args[0]);
                await postConnection.Connect(InterceptionMode.Post, args[0]);
                await executedConnection.Connect(InterceptionMode.Executed, args[0]);
            }
            Console.WriteLine("Connected!");

            // Start waiting for incoming codes
            Task[] tasks = new Task[] {
                PrintIncomingCodes("pre", preConnection),
                PrintIncomingCodes("post", postConnection),
                PrintIncomingCodes("executed", executedConnection)
            };

            // Wait for all tasks to finish
            await Task.WhenAll(tasks);
        }

        static async Task PrintIncomingCodes(string prefix, InterceptConnection connection)
        {
            Code code;
            do
            {
                code = await connection.ReceiveCode();
                if (code == null)
                {
                    // Lost connection
                    break;
                }

                // If you do not wish to let the received code execute, you can run connection.ResolveCode instead.
                // Before you call one of Cancel, Ignore, or Resolve you may execute as many commands as you want.
                // Codes initiated from the intercepting connection cannot be intercepted from the same connection.
                await connection.IgnoreCode();

                Console.WriteLine($"[{prefix}] {code.Channel}: {code}");
            }
            while (true);
        }
    }
}
