using DuetAPI.ObjectModel;
using DuetControlServer.Commands;
using DuetControlServer.FileExecution;
using DuetControlServer.Files;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
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
        /// Version of this application
        /// </summary>
        public static readonly string Version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;

        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Cancellation source that is triggered when the program is supposed to terminate
        /// </summary>
        private static readonly CancellationTokenSource _cancelSource = new();

        /// <summary>
        /// Global cancellation token that is triggered when the program is supposed to terminate
        /// </summary>
        public static readonly CancellationToken CancellationToken = _cancelSource.Token;

        /// <summary>
        /// Cancellation token to be called when the program has been terminated
        /// </summary>
        private static readonly CancellationTokenSource _programTerminated = new();

        /// <summary>
        /// Entry point of the program
        /// </summary>
        /// <param name="args">Command-line arguments</param>
        private static async Task Main(string[] args)
        {
            // Performing an update implies a reduced log level
            if (args.Contains("-u") && !args.Contains("--update"))
            {
                List<string> newArgs = new() { "--log-level", "error" };
                newArgs.AddRange(args);
                args = newArgs.ToArray();
            }
            else
            {
                Console.WriteLine($"Duet Control Server v{Version}");
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
                if (Settings.RootPluginSupport)
                {
                    _logger.Warn("Support for third-party root plugins is enabled");
                }
            }
            catch (Exception e)
            {
                _logger.Fatal(e, "Failed to load settings");
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
                Model.Provider.Init();
                Model.Observer.Init();
                await Utility.FilamentManager.Init();
                _logger.Info("Environment initialized");
            }
            catch (Exception e)
            {
                _logger.Fatal(e, "Failed to initialize environment");
                return;
            }

            // Set up SPI subsystem and connect to RRF controller
            if (Settings.NoSpi)
            {
                _logger.Warn("SPI connection to Duet is disabled");
            }
            else
            {
                try
                {
                    SPI.Interface.Init();
                    SPI.DataTransfer.Init();
                    _logger.Info("Connection to Duet established");
                }
                catch (Exception e)
                {
                    _logger.Fatal("Could not connect to Duet ({0})", e.Message);
                    _logger.Debug(e);
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
                _logger.Fatal(e, "Failed to initialize IPC socket");
                return;
            }

            // Start main tasks in the background
            Dictionary<Task, string> mainTasks = new()
            {
                { Task.Factory.StartNew(Model.Updater.Run, TaskCreationOptions.LongRunning).Unwrap(), "Update" },
                { Task.Factory.StartNew(SPI.Interface.Run, TaskCreationOptions.LongRunning).Unwrap(), "SPI" },
                { Task.Factory.StartNew(IPC.Server.Run, TaskCreationOptions.LongRunning).Unwrap(), "IPC" },
                { Task.Factory.StartNew(FileExecution.Job.Run, TaskCreationOptions.LongRunning).Unwrap(), "Job" },
                { Task.Factory.StartNew(Model.PeriodicUpdater.Run, TaskCreationOptions.LongRunning).Unwrap(), "Periodic updater" }
            };

            // Deal with program termination requests (SIGTERM and Ctrl+C)
            AssemblyLoadContext.Default.Unloading += (obj) =>
            {
                if (!_cancelSource.IsCancellationRequested)
                {
                    _logger.Warn("Received SIGTERM, shutting down...");
                    Shutdown(true).Wait();
                }
            };
            Console.CancelKeyPress += (sender, e) =>
            {
                if (!_cancelSource.IsCancellationRequested)
                {
                    _logger.Warn("Received SIGINT, shutting down...");
                    e.Cancel = true;
                    Shutdown().Wait();
                }
            };

            // Notify the service manager that we're up and running
            string notifySocket = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
            if (!string.IsNullOrEmpty(notifySocket))
            {
                try
                {
                    using Socket socket = new(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
                    socket.Connect(new UnixDomainSocketEndPoint(notifySocket));
                    socket.Send(System.Text.Encoding.UTF8.GetBytes("READY=1"));
                }
                catch (Exception e)
                {
                    _logger.Warn(e, "Failed to notify systemd about process start");
                }
            }

            if (!Settings.UpdateOnly)
            {
                // Load plugin manifests
                if (Settings.PluginSupport)
                {
                    foreach (string file in Directory.GetFiles(Settings.PluginDirectory))
                    {
                        if (file.EndsWith(".json"))
                        {
                            try
                            {
                                using FileStream manifestStream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                                using JsonDocument manifestJson = await JsonDocument.ParseAsync(manifestStream);
                                Plugin plugin = new();
                                plugin.UpdateFromJson(manifestJson.RootElement, false);
                                plugin.Pid = -1;
                                using (await Model.Provider.AccessReadWriteAsync())
                                {
                                    Model.Provider.Get.Plugins.Add(plugin.Id, plugin);
                                }
                            }
                            catch (Exception e)
                            {
                                _logger.Error(e, "Failed to load plugin manifest {0}", Path.GetFileName(file));
                            }
                        }
                    }
                }

                // Execute runonce.g after config.g if it is present
                string runOnceFile = await FilePath.ToPhysicalAsync(FilePath.RunOnceFile, FileDirectory.System);
                if (File.Exists(runOnceFile))
                {
                    do
                    {
                        using (await Model.Provider.AccessReadOnlyAsync())
                        {
                            if (Model.Provider.Get.State.Status != MachineStatus.Starting)
                            {
                                break;
                            }
                        }
                        await Task.Delay(250);
                    }
                    while (!CancellationToken.IsCancellationRequested);

                    if (!CancellationToken.IsCancellationRequested)
                    {
                        using (Macro macro = new(FilePath.RunOnceFile, runOnceFile, DuetAPI.CodeChannel.Trigger))
                        {
                            await macro.FinishAsync();
                        }

                        try
                        {
                            File.Delete(runOnceFile);
                        }
                        catch (Exception e)
                        {
                            await Model.Provider.Output(MessageType.Error, $"Failed to delete {FilePath.RunOnceFile}: {e.Message}");
                        }
                    }
                }
            }

            // Wait for the first task to terminate.
            // In case this is an unusual shutdown, log this event and shut down the application
            Task terminatedTask = await Task.WhenAny(mainTasks.Keys);
            if (!_cancelSource.IsCancellationRequested)
            {
                _logger.Fatal("Abnormal program termination");
                if (terminatedTask.IsFaulted)
                {
                    string taskName = mainTasks[terminatedTask];
                    _logger.Fatal(terminatedTask.Exception, "{0} task faulted", taskName);
                }

                // Stop the plugins again
                StopPlugins stopCommand = new();
                await stopCommand.Execute();

                // Shut down DCS
                _cancelSource.Cancel();
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
                    terminatedTask = await Task.WhenAny(mainTasks.Keys);
                }
            }
            while (mainTasks.Count > 0);

            // End
            _logger.Info("Application has shut down");
            NLog.LogManager.Shutdown();
            _programTerminated.Cancel();
        }

        /// <summary>
        /// Check if another instance is already running and send M997 if DSF is updating
        /// </summary>
        /// <returns>True if another instance is running</returns>
        private static async Task<bool> CheckForAnotherInstance()
        {
            using DuetAPIClient.CommandConnection connection = new();
            try
            {
                await connection.Connect(Settings.FullSocketPath);
            }
            catch (SocketException)
            {
                return false;
            }

            if (Settings.UpdateOnly)
            {
                Console.Write("Sending update request to DCS... ");
                try
                {
                    await connection.PerformCode(new Code
                    {
                        Channel = DuetAPI.CodeChannel.Trigger,
                        Type = DuetAPI.Commands.CodeType.MCode,
                        MajorNumber = 997
                    });
                    Console.WriteLine("Done!");
                }
                catch
                {
                    Console.WriteLine("Error: Failed to send update request");
                    throw;
                }
                finally
                {
                    _cancelSource.Cancel();
                }
            }
            else
            {
                _logger.Fatal("Another instance is already running. Stopping.");
            }
            return true;
        }

        /// <summary>
        /// Terminate this program and kill it forcefully if required
        /// </summary>
        /// <param name="waitForTermination">Wait for program to be fully terminated</param>
        /// <returns>Asynchronous task</returns>
        public static async Task Shutdown(bool waitForTermination = false)
        {
            // Shut down the plugins again
            try
            {
                StopPlugins stopCommand = new();
                await stopCommand.Execute();
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to stop plugins");
            }

            // Wait for potential firmware update to finish
            await SPI.Interface.WaitForUpdate();

            // Try to shut down this program normally 
            _cancelSource.Cancel();

            // If that fails, kill the program forcefully
            Task terminationTask = Task.Delay(4500, _programTerminated.Token).ContinueWith(async task =>
            {
                try
                {
                    await task;
                    Environment.Exit(1);
                }
                catch (OperationCanceledException)
                {
                    // expected
                }
            }, TaskContinuationOptions.RunContinuationsAsynchronously);

            // Wait for program termination if required
            if (waitForTermination)
            {
                await terminationTask;
            }
        }
    }
}
