using DuetAPI.Connection;
using DuetAPIClient;
using System;
using System.IO;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Threading.Tasks;

namespace CodeStream
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            // Parse the command line arguments
            string lastArg = null, socketPath = Defaults.FullSocketPath;
            bool quiet = false;
            int bufferSize = Defaults.CodeBufferSize;
            foreach (string arg in args)
            {
                if (lastArg == "-s" || lastArg == "--socket")
                {
                    socketPath = arg;
                }
                else if (lastArg == "-b" || lastArg == "--buffer-size")
                {
                    bufferSize = int.Parse(arg);
                    if (bufferSize < 1 || bufferSize > Defaults.MaxCodeBufferSize)
                    {
                        Console.WriteLine("Buffer size must be between 1 and {0}", Defaults.MaxCodeBufferSize);
                    }
                }
                else if (arg == "-q" || arg == "--quiet")
                {
                    quiet = true;
                }
                else if (arg == "-h" || arg == "--help")
                {
                    Console.WriteLine("Available command line arguments:");
                    Console.WriteLine("-s, --socket <socket>: UNIX socket to connect to");
                    Console.WriteLine("-b, --buffer-size <size>: Maximum number of codes to execute simultaneously");
                    Console.WriteLine("-q, --quiet: Do not output any messages (not applicable for code replies in interactive mode)");
                    Console.WriteLine("-h, --help: Display this help text");
                    return 0;
                }
                lastArg = arg;
            }

            // Create a new connection and connect to DuetControlServer
            using CodeStreamConnection connection = new();
            try
            {
                await connection.Connect(bufferSize, DuetAPI.CodeChannel.Telnet, socketPath);
            }
            catch (SocketException)
            {
                if (!quiet)
                {
                    Console.Error.WriteLine("Failed to connect to DCS");
                }
                return 1;
            }

            // Start streaming
            // This aapplication does not register a console session, see CodeConsole for further details about that
            if (!quiet)
            {
                // Notify the user that a connection has been established
                Console.WriteLine("Connected!");
            }

            await using NetworkStream stream = connection.GetStream();
            Task inputTask = Task.Run(async () => await ReadCodes(stream));     // This is started with Task.Run() because Console.ReadLine blocks...
            Task outputTask = WriteReplies(stream);
            await Task.WhenAny(inputTask, outputTask);

            if (connection.IsConnected)
            {
                connection.Close();
            }
            return 0;
        }

        /// <summary>
        /// Read codes from stdin and send them straight to DCS
        /// </summary>
        /// <param name="socketStream">Stream to write to</param>
        /// <returns>Asynchronous task</returns>
        private static async Task ReadCodes(Stream socketStream)
        {
            await using StreamWriter writer = new(socketStream);
            do
            {
                try
                {
                    string line = Console.ReadLine();
                    if (line == "exit" || line == "quit")
                    {
                        break;
                    }
                    await writer.WriteLineAsync(line);
                    await writer.FlushAsync();
                }
                catch (SocketException)
                {
                    break;
                }
            }
            while (true);
        }

        /// <summary>
        /// Read lines from the socket stream and output them to stdout
        /// </summary>
        /// <param name="socketStream">Stream to read from</param>
        /// <returns>Asynchronous task</returns>
        private static async Task WriteReplies(Stream socketStream)
        {
            using StreamReader reader = new(socketStream);
            do
            {
                try
                {
                    string line = await reader.ReadLineAsync();
                    Console.WriteLine(line);
                }
                catch (SocketException)
                {
                    break;
                }
            }
            while (true);
        }
    }
}
