using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer
{
    static class Program
    {
        public static CancellationTokenSource CancelSource = new CancellationTokenSource();

        static void Main(string[] args)
        {
            Console.WriteLine($"Duet Control Server v{Assembly.GetExecutingAssembly().GetName().Version}");
            Console.WriteLine("Written by Christian Hammacher for Duet3D");
            Console.WriteLine("Licensed under the terms of the GNU Public License Version 3");
            Console.WriteLine();
            
            // Deal with program termination requests (SIGTERM)
            AppDomain.CurrentDomain.ProcessExit += (sender, eventArgs) => CancelSource.Cancel();

            // Initialise the settings
            Console.Write("Loading settings... ");
            try
            {
                Settings.Load();
                Settings.ParseParameters(args);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error: {e.Message}");
                return;
            }

            // Connect to the controller
            Console.Write("Connecting to RepRapFirmware... ");
            try
            {
                RepRapFirmware.Connector.Connect();
                Console.WriteLine("Done!");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error: {e.Message}");
                return;
            }

            // Start up the IPC server
            Console.Write("Initialising IPC socket... ");
            try
            {
                IPC.Server.CreateSocket();
                Console.WriteLine("Done!");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error: {e.Message}");
                return;
            }

            // Run the main tasks in the background
            Task ipcTask = IPC.Server.AcceptConnections();
            Task rrfTask = RepRapFirmware.Connector.Run();
            Task[] taskList = new Task[] { ipcTask, rrfTask };

            // Wait for program termination
            Task.WaitAny(taskList, CancelSource.Token);

            // Tell other tasks to stop in case this is an abnormal program termination
            if (!CancelSource.IsCancellationRequested)
            {
                CancelSource.Cancel();
            }

            // Stop the IPC subsystem. This has to happen here because Socket.AcceptAsync() does not have a CancellationToken parameter
            IPC.Server.Shutdown();

            // Wait for all tasks to finish
            Task.WaitAll(taskList);
        }
    }
}
