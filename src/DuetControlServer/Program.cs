using DuetAPI.ObjectModel;
using DuetControlServer.Commands;
using DuetControlServer.Files;
using LinuxApi;
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
        public static readonly string Version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

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
        /// <returns>Application return code</returns>
        private static async Task<int> Main(string[] args)
        {
            // Performing an update implies a reduced log level
            if (args.Contains("-u") && !args.Contains("--update"))
            {
                List<string> newArgs = ["--log-level", "error", .. args];
                args = [.. newArgs];
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
                    // This must be a benign termination request
                    return ExitCode.TempFailure;
                }
                _logger.Info("Settings loaded");
                if (Settings.RootPluginSupport)
                {
                    _logger.Warn("Support for third-party root plugins is enabled");
                }
            }
            catch (JsonException je)
            {
                await Terminate($"Failed to load settings: {je.Message}");
                _logger.Debug(je);
                return ExitCode.Configuration;

            }
            catch (Exception e)
            {
                await Terminate($"Failed to initialize settings: {e.Message}");
                _logger.Debug(e);
                return ExitCode.Usage;
            }

            // Check if another instance is already running
            if (await CheckForAnotherInstance())
            {
                // No need to log the start-up failure here
                return Settings.UpdateOnly ? ExitCode.Success : ExitCode.TempFailure;
            }

            // Initialize everything
            try
            {
                Codes.Handlers.Functions.Init();
                Model.Provider.Init();
                Model.Observer.Init();
                _logger.Info("Environment initialized");
            }
            catch (Exception e)
            {
                await Terminate($"Failed to initialize environment: {e.Message}");
                _logger.Debug(e);
                return ExitCode.OsError;
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
                    SPI.DataTransfer.Init();
                    _logger.Info("Connection to Duet established");
                }
                catch (IOException ioe)
                {
                    await Terminate($"Failed to open IO device: {ioe.Message}");
                    _logger.Debug(ioe);
                    return ExitCode.IoError;
                }
                catch (Exception e)
                {
                    await Terminate($"Could not connect to Duet: {e.Message}");
                    _logger.Debug(e);
                    return ExitCode.ServiceUnavailable;
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
                await Terminate($"Failed to initialize IPC socket ({e.Message})");
                _logger.Debug(e);
                return ExitCode.CantCreate;
            }

            // Start main tasks in the background
            Dictionary<Task, string> mainTasks = new()
            {
                { Task.Factory.StartNew(Codes.Processor.Run, TaskCreationOptions.LongRunning).Unwrap(), "Code processor" },
                { Utility.PriorityThreadRunner.Start(SPI.Interface.Run, ThreadPriority.Highest), "SPI" },
                { Task.Factory.StartNew(Model.Updater.Run, TaskCreationOptions.LongRunning).Unwrap(), "Update" },
                { Task.Factory.StartNew(IPC.Server.Run, TaskCreationOptions.LongRunning).Unwrap(), "IPC" },
                { Task.Factory.StartNew(JobProcessor.Run, TaskCreationOptions.LongRunning).Unwrap(), "Job" },
                { Task.Factory.StartNew(Model.PeriodicUpdater.Run, TaskCreationOptions.LongRunning).Unwrap(), "Periodic updater" }
            };

            // Deal with program termination requests (SIGTERM and Ctrl+C)
            AssemblyLoadContext.Default.Unloading += _ =>
            {
                if (!_cancelSource.IsCancellationRequested)
                {
                    _logger.Warn("Received SIGTERM, shutting down...");
                    try
                    {
                        // Wait for potential firmware update to finish
                        SPI.Interface.WaitForUpdate();

                        // Shut down this instance after 4.5s tops
                        using CancellationTokenSource cts = new(4500);
                        ShutdownAsync(true).Wait(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.Fatal("Regular shutdown failed, proceeding with unconditional program termination");
                        NLog.LogManager.Shutdown();
                    }
                }
            };
            Console.CancelKeyPress += (sender, e) =>
            {
                if (!_cancelSource.IsCancellationRequested)
                {
                    _logger.Warn("Received SIGINT, shutting down...");
                    e.Cancel = true;
                    _  = ShutdownAsync();
                }
            };

            // Notify the service manager that we're up and running
            string? notifySocket = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
            if (!string.IsNullOrEmpty(notifySocket))
            {
                try
                {
                    using Socket socket = new(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(notifySocket));
                    socket.Send(System.Text.Encoding.UTF8.GetBytes("READY=1"));
                }
                catch (Exception e)
                {
                    _logger.Warn(e, "Failed to notify systemd about process start");
                }
            }

            // Delete the last DCS error file if it exists
            if (File.Exists(Settings.StartErrorFile))
            {
                File.Delete(Settings.StartErrorFile);
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
                                await using FileStream manifestStream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, Settings.FileBufferSize);
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
                string runOnceFile = await FilePath.ToPhysicalAsync(FilePath.RunOnceFile, DuetAPI.Commands.FileDirectory.System);
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
                        using MacroFile? macro = MacroFile.Open(FilePath.RunOnceFile, runOnceFile, DuetAPI.CodeChannel.Trigger);
                        if (macro is not null)
                        {
                            macro.Start();
                            await macro.WaitForFinishAsync();
                        }

                        try
                        {
                            File.Delete(runOnceFile);
                        }
                        catch (Exception e)
                        {
                            await Model.Provider.OutputAsync(MessageType.Error, $"Failed to delete {FilePath.RunOnceFile}: {e.Message}");
                        }
                    }
                }
            }

            // Wait for the first task to terminate.
            // In case this is an unusual shutdown, log this event and shut down the application
            bool abnormalTermination = false;
            Task terminatedTask = await Task.WhenAny(mainTasks.Keys);
            if (!_cancelSource.IsCancellationRequested)
            {
                abnormalTermination = true;
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
                await SPI.Interface.ShutdownAsync();
                _cancelSource.Cancel();
            }

            // Wait for the other tasks to finish
            do
            {
                string taskName = mainTasks[terminatedTask];
                if (terminatedTask.IsFaulted && !terminatedTask.IsCanceled)
                {
                    foreach (Exception ie in terminatedTask.Exception!.InnerExceptions)
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
            return abnormalTermination ? ExitCode.Software : ExitCode.Success;
        }

        /// <summary>
        /// Check if another instance is already running and send M997 if DSF is updating
        /// </summary>
        /// <returns>True if another instance is running</returns>
        private static async Task<bool> CheckForAnotherInstance()
        {
            try
            {
                using DuetAPIClient.CommandConnection connection = new();
                await connection.Connect(Settings.FullSocketPath);
            }
            catch (SocketException)
            {
                return false;
            }

            if (Settings.UpdateOnly)
            {
                try
                {
                    await Utility.Firmware.UpdateFirmwareRemotely();
                }
                finally
                {
                    // SPI subsystem is not running at this point
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
        /// Print the reason for the start error and write it to the start error file
        /// </summary>
        /// <param name="reason">Reason for the program termination</param>
        /// <returns>Asynchronous task</returns>
        private static async Task Terminate(string reason)
        {
            _logger.Fatal(reason);
            await File.WriteAllTextAsync(Settings.StartErrorFile, reason);
        }

        /// <summary>
        /// Don't attempt to shut down multiple times at once
        /// </summary>
        private static bool _shuttingDown;

        /// <summary>
        /// Terminate this program and kill it forcefully if required
        /// </summary>
        /// <param name="waitForTermination">Wait for program to be fully terminated</param>
        /// <returns>Asynchronous task</returns>
        public static async Task ShutdownAsync(bool waitForTermination = false)
        {
            // Are we already shutting down?
            if (_shuttingDown)
            {
                if (waitForTermination)
                {
                    try
                    {
                        await Task.Delay(-1, _programTerminated.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // expected
                    }
                }
                return;
            }
            _shuttingDown = true;

            // Shut down the plugins again. This must happen before the cancellation token is triggered
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
            await SPI.Interface.WaitForUpdateAsync();

            // Make sure the program is terminated within 5s
            Task watchdogTask = Task.Run(async delegate
            {
                try
                {
                    await Task.Delay(5000, _programTerminated.Token);
                    Environment.Exit(ExitCode.Software);
                }
                catch (OperationCanceledException)
                {
                    // expected
                }
            });

            // Try to shut down this program normally
            await SPI.Interface.ShutdownAsync();
            _cancelSource.Cancel();

            // Wait for program termination if required
            if (waitForTermination)
            {
                await watchdogTask;
            }
        }
    }
}
