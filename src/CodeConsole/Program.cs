using DuetAPI.Connection;
using DuetAPIClient;
using System;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace CodeConsole
{
    public static class Program
    {
        public static async Task Main(string[] args)
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
                    Console.WriteLine("-c, --code <code>: Execute the given code(s), wait for the result and exit");
                    Console.WriteLine("-q, --quiet: Do not display when a connection has been established (only applicable in interactive mode)");
                    Console.WriteLine("-h, --help: Display this help text");
                    return;
                }
                lastArg = arg;
            }

            // Create a new connection and connect to DuetControlServer
            using CommandConnection connection = new CommandConnection();
            await connection.Connect(socketPath);

            // Check if this is an interactive session
            if (codeToExecute == null)
            {
                if (!quiet)
                {
                    // Notify the user that a connection has been established
                    Console.WriteLine("Connected!");
                }

                // Register an (interactive) user session
                int sessionId = await connection.AddUserSession(DuetAPI.Machine.AccessLevel.ReadWrite, DuetAPI.Machine.SessionType.Local, "console");

                // Start reading lines from stdin and send them to DCS as simple codes.
                // When the code has finished, the result is printed to stdout
                string input = Console.ReadLine();
                while (input != null && input != "exit" && input != "quit")
                {
                    try
                    {
                        string output = await connection.PerformSimpleCode(input);
                        Console.Write(output);
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
            else
            {
                // Execute only the given code(s) and quit
                string output = await connection.PerformSimpleCode(codeToExecute);
                Console.Write(output);
            }
        }
    }
}
