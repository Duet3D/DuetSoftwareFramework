using System;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DuetControlServer.IPC;

namespace DuetControlServer
{
    /// <summary>
    /// Main program class
    /// </summary>
    static class Program
    {
        /// <summary>
        /// Global cancel source for program termination
        /// </summary>
        public static readonly CancellationTokenSource CancelSource = new CancellationTokenSource();

        /// <summary>
        /// Entry point of the program
        /// </summary>
        /// <param name="args">Command-line arguments</param>
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
                Model.Provider.Init();
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
                SPI.Interface.Init();
                if (!SPI.Interface.Connect().Result)
                {
                    Console.WriteLine("Error: Duet is not available");
                    return;
                }
                Console.WriteLine("Done!");
            }
            catch (AggregateException ae)
            {
                Console.WriteLine($"Error: {ae.InnerException.Message}");
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
            
            Console.WriteLine();

            // Run the main tasks in the background
            Task spiTask = SPI.Interface.Run();
            Task ipcTask = Server.AcceptConnections();
            Task modelUpdateTask = Model.UpdateTask.UpdatePeriodically();
            Task[] taskList = { spiTask, ipcTask, modelUpdateTask };

            // Wait for program termination
            Task.WaitAny(taskList);

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
            catch (AggregateException ae)
            {
                foreach (Exception e in ae.InnerExceptions)
                {
                    if (!(e is OperationCanceledException) && !(e is SocketException))
                    {
                        Console.WriteLine(e);
                    }
                }
            }
        }
    }
}
