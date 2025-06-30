using DuetControlServer.Codes;
using DuetControlServer.Commands;
using DuetControlServer.Files;
using DuetControlServer.IPC;
using DuetControlServer.Link;
using DuetControlServer.Model;
using DuetControlServer.Utility;
using DuetSharedLibrary;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Hosting;
using System.CommandLine.NamingConventionBinder;
using System.CommandLine.Parsing;
using System.Reflection;
using System.Threading.Tasks;

namespace DuetControlServer;

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
    /// Exit code of the program
    /// </summary>
    private static int _exitCode = ExitCode.Success;

    /// <summary>
    /// Set the exit code of the program
    /// </summary>
    /// <param name="exitCode">Exit code to be set</param>
    public static void SetExitCode(int exitCode) => _exitCode = exitCode;

    /// <summary>
    /// Entry point of the program
    /// </summary>
    /// <param name="args">Command-line arguments</param>
    /// <returns>Application return code</returns>
    private static async Task<int> Main(string[] args)
    {
        var configOption = new Option<string>(
            ["-c", "--config"],
            description: "Path to the configuration file",
            getDefaultValue: () => Settings.DefaultConfigFile);

        var updateCommand = new Command("update", "Update RepRapFirmware and exit");

        var rootCommand = new RootCommand("Duet Control Server")
        {
            configOption
        };
        rootCommand.Handler = CommandHandler.Create<IHost, Settings>(async (host, settings) =>
        {
            Console.WriteLine($"Duet Control Server v{Version}");
            Console.WriteLine("Written by Christian Hammacher for Duet3D");
            Console.WriteLine("Licensed under the terms of the GNU Public License Version 3");
            Console.WriteLine();

#if false
            // Check if another instance is already running
            if (await CheckForAnotherInstance(settings.FullSocketPath, settings.UpdateOnly))
            {
                // No need to log the start-up failure here
                return settings.UpdateOnly ? ExitCode.Success : ExitCode.TempFailure;
            }
#endif

            // Start host application and wait for shutdown
            await host.WaitForShutdownAsync();
            return _exitCode;
        });

        rootCommand.Add(updateCommand);

        string configFile = Settings.DefaultConfigFile;
        return await new CommandLineBuilder(rootCommand)
            .AddMiddleware((context) =>
            {
                context.ParseResult.GetValueForOption(configOption); ;
                configFile = context.ParseResult.GetValueForOption(configOption)!;
            })
            .UseHost(builder => builder
                .UseSystemd()
                .ConfigureAppConfiguration((hostingContext, config) =>
                {
                    config
                        .AddJsonFile(configFile, optional: true)
                        .AddCommandLine(args);
                })
                .ConfigureServices((context, services) => services
                    .AddSettings(context.Configuration)
                    .AddCodes()
                    .AddCommands()
                    .AddFiles()
                    .AddIPC()
                    .AddLink()
                    .AddModel()
                    .AddSPILink()
                    .AddUtility()
                )
            )
            .UseDefaults()
            .Build()
            .InvokeAsync(args);
    }
}

#if false
        // Performing an update implies a reduced log level
        if (args.Contains("-u") && !args.Contains("--update
        {
            List<string> newArgs = ["--log-level", "error", .. args];
            args = [.. newArgs];
        }
        else
        {
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
                Link.DataTransfer.Init();
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
            { Task.Factory.StartNew(Codes.CodeProcessor.Run, TaskCreationOptions.LongRunning).Unwrap(), "Code processor" },
            { Utility.PriorityThreadRunner.Start(Link.Interface.Run, ThreadPriority.Highest), "SPI" },
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
                    Link.Interface.WaitForUpdate();

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
                _ = ShutdownAsync();
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
                            plugin.Started = false;
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
            string runOnceFile = await FilePathResolver.ToPhysicalAsync(FilePathResolver.RunOnceFile, DuetAPI.Commands.FileDirectory.System);
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
                    using MacroFile? macro = MacroFile.Open(FilePathResolver.RunOnceFile, runOnceFile, DuetAPI.CodeChannel.Trigger);
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
                        await Model.Provider.OutputAsync(MessageType.Error, $"Failed to delete {FilePathResolver.RunOnceFile}: {e.Message}");
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
            await stopCommand.ExecuteAsync();

            // Shut down DCS
            await Link.Interface.ShutdownAsync();
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
    /// <param name="socketPath">Path to the IPC socket</param>
    /// <param name="updateOnly">True if the update command was given</param>
    /// <returns>True if another instance is running</returns>
    private static async Task<bool> CheckForAnotherInstance(string socketPath, bool updateOnly)
    {
        try
        {
            using DuetAPIClient.CommandConnection connection = new();
            await connection.ConnectAsync(socketPath);
        }
        catch (SocketException)
        {
            return false;
        }

        if (updateOnly)
        {
            await Utility.Firmware.UpdateFirmwareRemotely();
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
            await stopCommand.ExecuteAsync();
        }
        catch (Exception e)
        {
            _logger.Error(e, "Failed to stop plugins");
        }

        // Wait for potential firmware update to finish
        await Link.Interface.WaitForUpdateAsync();

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
        await Link.Interface.ShutdownAsync();
        _cancelSource.Cancel();

        // Wait for program termination if required
        if (waitForTermination)
        {
            await watchdogTask;
        }
    }
}
#endif