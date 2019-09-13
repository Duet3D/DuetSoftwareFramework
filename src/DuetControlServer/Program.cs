using System;
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
        /// <returns>Asynchronous task</returns>
        static async Task Main(string[] args)
        {
            Console.WriteLine($"Duet Control Server v{Assembly.GetExecutingAssembly().GetName().Version}");
            Console.WriteLine("Written by Christian Hammacher for Duet3D");
            Console.WriteLine("Licensed under the terms of the GNU Public License Version 3");
            Console.WriteLine();

            // Initialize settings
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

            // Check if another instance is already running
            using (DuetAPIClient.CommandConnection testConnection = new DuetAPIClient.CommandConnection())
            {
                try
                {
                    await testConnection.Connect(Settings.SocketPath);
                    if (Settings.UpdateOnly)
                    {
                        Console.Write("Another instance is already running, sending update request to it... ");
                        try
                        {
                            await testConnection.PerformCode(new DuetAPI.Commands.Code
                            {
                                Type = DuetAPI.Commands.CodeType.MCode,
                                MajorNumber = 997,
                                Flags = DuetAPI.Commands.CodeFlags.IsPrioritized
                            });
                            Console.WriteLine("Done!");
                        }
                        catch (Exception e)
                        {
                            Console.Write("Error: ");
                            Console.WriteLine(e);
                        }
                    }
                    else
                    {
                        Console.WriteLine("Error: Another instance is already running. Stopping.");
                    }
                    return;
                }
                catch
                {
                    // expected
                }
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

            // Connect to RRF controller
            Console.Write("Connecting to RepRapFirmware... ");
            try
            {
                SPI.Interface.Init();
                if (!SPI.Interface.Connect())
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

            // Start up IPC server
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

            // Start main tasks in the background
            Task spiTask = Task.Run(SPI.Interface.Run, CancelSource.Token);
            Task ipcTask = Task.Run(Server.AcceptConnections, CancelSource.Token);
            Task modelUpdateTask = Task.Run(Model.UpdateTask.UpdatePeriodically, CancelSource.Token);
            Task codeTask = Task.Run(Codes.Execution.ProcessCodes, CancelSource.Token);
            Task[] taskList = { spiTask, ipcTask, modelUpdateTask, codeTask };

            // Deal with program termination requests (SIGTERM) and wait for program termination
            AppDomain.CurrentDomain.ProcessExit += (sender, eventArgs) =>
            {
                if (!CancelSource.IsCancellationRequested)
                {
                    Console.WriteLine("[info] Received SIGTERM, shutting down...");
                    CancelSource.Cancel();
                }
            };

            await Task.WhenAny(taskList);

            // Tell other tasks to stop in case this is an abnormal program termination
            if (Settings.UpdateOnly)
            {
                CancelSource.Cancel();
            }
            else if (!CancelSource.IsCancellationRequested)
            {
                Console.Write("[crit] Abnormal program termination: ");
                if (spiTask.IsCompleted) { Console.WriteLine("SPI task terminated");  }
                if (ipcTask.IsCompleted) { Console.WriteLine("IPC task terminated");  }
                if (modelUpdateTask.IsCompleted) { Console.WriteLine("Model task terminated"); }
                if (codeTask.IsCompleted) { Console.WriteLine("Code task terminated"); }
                CancelSource.Cancel();
            }

            // Stop logging
            await Utility.Logger.Stop();

            // Stop the IPC subsystem. This has to happen here because Socket.AcceptAsync() does not have a CancellationToken parameter
            Server.Shutdown();

            // Wait for all tasks to finish
            try
            {
                // Do not use Task.WhenAll() here because it does not throw exceptions for already completed tasks
                Task.WaitAll(taskList);
            }
            catch (AggregateException ae)
            {
                foreach (Exception e in ae.InnerExceptions)
                {
                    if (!(e is OperationCanceledException))
                    {
                        Console.Write("[crit] ");
                        Console.WriteLine(e);
                    }
                }
            }
        }
    }
}
