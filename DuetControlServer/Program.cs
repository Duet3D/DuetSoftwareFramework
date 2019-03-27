using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DuetControlServer.IPC;
using DuetControlServer.SPI;

namespace DuetControlServer
{
    static class Program
    {
        public static readonly CancellationTokenSource CancelSource = new CancellationTokenSource();

        static void Main(string[] args)
        {
            Console.WriteLine($"Duet Control Server v{Assembly.GetExecutingAssembly().GetName().Version}");
            Console.WriteLine("Written by Christian Hammacher for Duet3D");
            Console.WriteLine("Licensed under the terms of the GNU Public License Version 3");
            Console.WriteLine();
            
            // Deal with program termination requests (SIGTERM)
            AppDomain.CurrentDomain.ProcessExit += (sender, eventArgs) => CancelSource.Cancel();

            // Initialise settings
            Console.Write("Loading settings... ");
            try
            {
                Settings.Load(args);
                Console.WriteLine("Done!");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error: {e.Message}");
                return;
            }
            
            // Initialise object model
            Console.Write("Initialising object model... ");
            try
            {
                ModelProvider.Update();
                Console.WriteLine("Done!");
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
                Connector.Connect();
                Console.WriteLine("Done!");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error: {e.Message}");
                return;
            }

            // Start up the IPC server
            Console.Write("Creating IPC socket... ");
            try
            {
                Server.CreateSocket();
                Console.WriteLine("Done!");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error: {e.Message}");
                return;
            }
            
            // Add an empty line so NLog can take over from here on
            Console.WriteLine();
            
            // Run the main tasks in the background
            Task ipcTask = Server.AcceptConnections();
            Task rrfTask = Connector.Run();
            Task[] taskList = { ipcTask, rrfTask };

            // Wait for program termination
            try
            {
                Task.WaitAny(taskList);
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                throw;
            }

            // Tell other tasks to stop in case this is an abnormal program termination
            if (!CancelSource.IsCancellationRequested)
            {
                CancelSource.Cancel();
            }

            // Stop the IPC subsystem. This has to happen here because Socket.AcceptAsync() does not have a CancellationToken parameter
            Server.Shutdown();

            // Wait for all tasks to finish
            try
            {
                Task.WaitAll(taskList);
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                throw;
            }
        }
    }
}
