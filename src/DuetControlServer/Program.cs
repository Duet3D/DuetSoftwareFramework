using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer
{
    /// <summary>
    /// Main program class
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Global cancellation source that is triggered when the program is supposed to terminate
        /// </summary>
        public static CancellationTokenSource CancelSource { get; } = new CancellationTokenSource();

        /// <summary>
        /// Entry point of the program
        /// </summary>
        /// <param name="args">Command-line arguments</param>
        private static async Task Main(string[] args)
        {
            // Performing an update implies a reduced log level
            if (args.Contains("-u") && !args.Contains("--update"))
            {
                List<string> newArgs = new List<string>() { "--log-level", "warn" };
                newArgs.AddRange(args);
                args = newArgs.ToArray();
            }
            else
            {
                Console.WriteLine($"Duet Control Server v{Assembly.GetExecutingAssembly().GetName().Version}");
                Console.WriteLine("Written by Christian Hammacher for Duet3D");
                Console.WriteLine("Licensed under the terms of the GNU Public License Version 3");
                Console.WriteLine();
            }

            // Initialize settings
            try
            {
                if (!Settings.Init(args))
                {
                    return;
                }
                _logger.Info("Settings loaded");
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to load settings");
                return;
            }

            // Check if another instance is already running
            if (await CheckForAnotherInstance())
            {
                return;
            }

            // Initialize everything
            try
            {
                Commands.Code.InitScheduler();
                Model.Provider.Init();
                Model.Observer.Init();
                _logger.Info("Environment initialized");
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to initialize environment");
                return;
            }

            // Connect to RRF controller
            if (Settings.NoSpiTask)
            {
                _logger.Warn("Connection to Duet is NOT established, things may not work as expected");
            }
            else
            {
                try
                {
                    SPI.Interface.Init();
                    if (SPI.Interface.Connect())
                    {
                        _logger.Info("Connection to Duet established");
                    }
                    else
                    {
                        _logger.Error("Duet is not available");
                        return;
                    }
                }
                catch (Exception e)
                {
                    _logger.Error(e, "Failed to connect to Duet");
                    return;
                }
            }

            // Start up IPC server
            try
            {
                IPC.Server.Init();
                _logger.Info("IPC socket created at {0}", Settings.FullSocketPath);
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to initialize IPC socket");
                return;
            }

            // Start main tasks in the background
            Dictionary<Task, string> mainTasks = new Dictionary<Task, string>
            {
                { Task.Run(Model.Updater.Run), "Update" },
                { Task.Run(SPI.Interface.Run), "SPI" },
                { Task.Run(IPC.Server.Run), "IPC" },
                { Task.Run(Model.PeriodicUpdater.Run), "Periodic update" }
            };

            // Deal with program termination requests (SIGTERM and Ctrl+C)
            AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
            {
                if (!CancelSource.IsCancellationRequested)
                {
                    _logger.Warn("Received SIGTERM, shutting down...");
                    CancelSource.Cancel();
                }
            };
            Console.CancelKeyPress += (sender, e) =>
            {
                if (!CancelSource.IsCancellationRequested)
                {
                    _logger.Warn("Received SIGINT, shutting down...");
                    e.Cancel = true;
                    CancelSource.Cancel();
                }
            };
            
            // Wait for the first task to terminate.
            // In case this is an unusual shutdown, log this event and shut down the application
            Task terminatedTask = await Task.WhenAny(mainTasks.Keys);
            if (!CancelSource.IsCancellationRequested)
            {
                if (!Settings.UpdateOnly)
                {
                    _logger.Fatal("Abnormal program termination");
                }
                CancelSource.Cancel();
            }

            // Wait for the other tasks to finish
            do
            {
                string taskName = mainTasks[terminatedTask];
                if (terminatedTask.IsFaulted && !terminatedTask.IsCanceled)
                {
                    foreach (Exception ie in terminatedTask.Exception.InnerExceptions)
                    {
                        _logger.Fatal(ie, "{0} task faulted", taskName);
                    }
                }
                else
                {
                    _logger.Debug("{0} task terminated", taskName);
                }

                mainTasks.Remove(terminatedTask);
                if (mainTasks.Count > 0)
                {
                    // FIXME: At present, calls to Task.WhenAny in this context may cause the .NET Core application to
                    // terminate suddently and without an exception. Hence the output may be truncated when shutting down
                    terminatedTask = await Task.WhenAny(mainTasks.Keys);
                }
            }
            while (mainTasks.Count > 0);

            // End
            _logger.Debug("Application has shut down");
            NLog.LogManager.Shutdown();
        }

        private static void CurrentDomain_ProcessExit(object sender, EventArgs e)
        {
            if (!CancelSource.IsCancellationRequested)
            {
                _logger.Warn("Received SIGTERM, shutting down...");
                CancelSource.Cancel();
            }
        }

        /// <summary>
        /// Check if another instance is already running
        /// </summary>
        /// <returns>True if another instance is running</returns>
        private static async Task<bool> CheckForAnotherInstance()
        {
            using (DuetAPIClient.CommandConnection connection = new DuetAPIClient.CommandConnection())
            {
                try
                {
                    await connection.Connect(Settings.FullSocketPath);
                    if (Settings.UpdateOnly)
                    {
                        Console.Write("Sending update request to DCS... ");
                        try
                        {
                            await connection.PerformCode(new DuetAPI.Commands.Code
                            {
                                Type = DuetAPI.Commands.CodeType.MCode,
                                MajorNumber = 997,
                                Flags = DuetAPI.Commands.CodeFlags.IsPrioritized
                            });
                            Console.WriteLine("Done!");
                        }
                        catch
                        {
                            Console.WriteLine("Error: Failed to send update request");
                            throw;
                        }
                    }
                    else
                    {
                        Console.WriteLine("Error: Another instance is already running. Stopping.");
                    }
                    return true;
                }
                catch
                {
                    // expected
                }
            }
            return false;
        }
    }
}
