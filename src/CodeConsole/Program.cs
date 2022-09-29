using DuetAPI.Connection;
using DuetAPIClient;
using System;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

namespace CodeConsole
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            // Parse the command line arguments
            string lastArg = null, codeToExecute = null, socketPath = Defaults.FullSocketPath;
            bool quiet = false;
            foreach (string arg in args)
            {
                if (lastArg == "-s" || lastArg == "--socket")
                {
                    socketPath = arg;
                }
                else if (lastArg == "-c" || lastArg == "-c")
                {
                    codeToExecute = arg;
                }
                else if (arg == "-q" || arg == "--quiet")
                {
                    quiet = true;
                }
                else if (arg == "-h" || arg == "--help")
                {
                    Console.WriteLine("Available command line arguments:");
                    Console.WriteLine("-s, --socket <socket>: UNIX socket to connect to");
                    Console.WriteLine("-c, --code <code>: Execute the given code(s), wait for the result and exit. Alternative codes: startUpdate (Set DSF to updating), endUpdate (End DSF updating state)");
                    Console.WriteLine("-q, --quiet: Do not output any messages (not applicable for code replies in interactive mode)");
                    Console.WriteLine("-h, --help: Display this help text");
                    return 0;
                }
                lastArg = arg;
            }

            // Create a new connection and connect to DuetControlServer
            using CommandConnection connection = new();
            try
            {
                await connection.Connect(socketPath);
            }
            catch (SocketException)
            {
                if (!quiet)
                {
                    Console.Error.WriteLine("Failed to connect to DCS");
                }
                return 1;
            }

            // Check if this is an interactive session
            if (codeToExecute == null)
            {
                if (!quiet)
                {
                    // Notify the user that a connection has been established
                    Console.WriteLine("Connected!");
                }

                // Register an (interactive) user session
                int sessionId = await connection.AddUserSession(DuetAPI.ObjectModel.AccessLevel.ReadWrite, DuetAPI.ObjectModel.SessionType.Local, "console");

                // Start reading lines from stdin and send them to DCS as simple codes.
                // When the code has finished, the result is printed to stdout
                string input = Console.ReadLine();
                while (input != null && input != "exit" && input != "quit")
                {
                    try
                    {
                        if (input.Equals("startUpdate", StringComparison.InvariantCultureIgnoreCase))
                        {
                            await connection.SetUpdateStatus(true);
                            Console.WriteLine("DSF is now in update mode");
                        }
                        else if (input.Equals("endUpdate", StringComparison.InvariantCultureIgnoreCase))
                        {
                            await connection.SetUpdateStatus(false);
                            Console.WriteLine("DSF is no longer in update mode");
                        }
                        else if (input.StartsWith("eval ", StringComparison.InvariantCultureIgnoreCase))
                        {
                            JsonElement result = await connection.EvaluateExpression<JsonElement>(input[5..].Trim());
                            Console.WriteLine("Evaluation result: {0}", result.GetRawText());
                        }
                        else
                        {
                            string output = await connection.PerformSimpleCode(input, DuetAPI.CodeChannel.Telnet);
                            if (output.EndsWith(Environment.NewLine))
                            {
                                Console.Write(output);
                            }
                            else
                            {
                                Console.WriteLine(output);
                            }
                        }
                    }
                    catch (SocketException)
                    {
                        Console.WriteLine("Server has closed the connection");
                        break;
                    }
                    catch (Exception e)
                    {
                        if (e is AggregateException ae)
                        {
                            e = ae.InnerException;
                        }
                        Console.WriteLine(e.Message);
                    }
                    input = Console.ReadLine();
                }

                // Unregister this session again
                if (connection.IsConnected)
                {
                    await connection.RemoveUserSession(sessionId);
                }
            }
            else if (codeToExecute.Equals("startUpdate", StringComparison.InvariantCultureIgnoreCase))
            {
                await connection.SetUpdateStatus(true);
                if (!quiet)
                {
                    Console.WriteLine("DSF is now in update mode");
                }
            }
            else if (codeToExecute.Equals("endUpdate", StringComparison.InvariantCultureIgnoreCase))
            {
                await connection.SetUpdateStatus(false);
                if (!quiet)
                {
                    Console.WriteLine("DSF is no longer in update mode");
                }
            }
            else
            {
                // Execute only the given code(s) and quit
                string output = await connection.PerformSimpleCode(codeToExecute);
                if (!quiet)
                {
                    if (output.EndsWith('\n'))
                    {
                        Console.Write(output);
                    }
                    else
                    {
                        Console.WriteLine(output);
                    }
                }
            }
            return 0;
        }
    }
}
