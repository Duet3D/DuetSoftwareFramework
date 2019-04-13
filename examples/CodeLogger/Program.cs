using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPIClient;
using System;
using System.Threading.Tasks;

namespace CodeLogger
{
    class Program
    {
        static void Main(string[] args)
        {
            // Connect to DCS
            InterceptConnection preConnection = new InterceptConnection();
            InterceptConnection postConnection = new InterceptConnection();
            if (args.Length == 0)
            {
                preConnection.Connect(InterceptionMode.Pre).Wait();
                postConnection.Connect(InterceptionMode.Post).Wait();
            }
            else
            {
                preConnection.Connect(InterceptionMode.Pre, args[0]).Wait();
                postConnection.Connect(InterceptionMode.Post, args[0]).Wait();
            }
            Console.WriteLine("Connected!");

            // Start two tasks waiting for codes to be received
            Task[] tasks = new [] {
                PrintIncomingCodes("pre", preConnection),
                PrintIncomingCodes("post", postConnection)
            };
            Task.WaitAll(tasks);
        }

        static async Task PrintIncomingCodes(string prefix, InterceptConnection connection)
        {
            while (true)
            {
                Code code = await connection.ReceiveCode();
                // If you do not wish to let the received code execute, you can run connection.ResolveCode instead.
                // Before you call either Ignore or Resolve, you may execute as many commands as you want.
                // Codes initiated from the intercepting connection cannot be intercepted from the same connection.
                await connection.IgnoreCode();
                Console.WriteLine($"[{prefix}] {code.Channel}: {code}");
            }
        }
    }
}
