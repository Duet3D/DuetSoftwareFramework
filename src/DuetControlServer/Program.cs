using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetControlServer;
using DuetControlServer.Codes;
using DuetControlServer.Commands;
using DuetControlServer.Files;
using DuetControlServer.IPC;
using DuetControlServer.Link;
using DuetControlServer.Model;
using DuetControlServer.Utility;
using DuetSharedLibrary;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NLog;
using NLog.Extensions.Hosting;
using System;
using System.CommandLine;
using System.IO;
using System.Text.Json;

string? startErrorFile = Defaults.StartErrorFile;

/// <summary>
/// Print the reason for the start error, write it to the start error file, and exit this application
/// </summary>
/// <param name="e">Exception that caused the termination</param>
/// <param name="reason">Reason for the program termination</param>
/// <param name="exitCode">Exit code</param>
void Terminate(Exception e, string reason, int exitCode)
{
    LogManager.GetCurrentClassLogger().Fatal(e, reason);
    File.WriteAllText(startErrorFile, reason);
    Environment.Exit(exitCode);
}

Option<bool> updateOnlyOption = new("--update", "-u")
{
    Description = "Update RepRapFirmware and exit. This works even if another instance is already started"
};
Option<NLog.LogLevel> logLevelOption = new("--log-level", "-l")
{
    Description = "Set the log level for the application"
};
Option<FileInfo> configFileOption = new("--config", "-c")
{
    Description = "Path to the configuration file"
};
Option<DirectoryInfo> socketDirectoryOption = new("--socket-directory", "-S")
{
    Description = "Directory to create the IPC socket in"
};
Option<string> socketFileOption = new("--socket-file", "-s")
{
    Description = "Full path to the IPC socket file",
    DefaultValueFactory = _ => Defaults.FullSocketPath
};
Option<DirectoryInfo> baseDirectoryOption = new("--base-directory", "-b")
{
    Description = "Base directory for the application, used to resolve relative paths"
};

RootCommand rootCommand = new("Duet Control Server")
{
    updateOnlyOption,
    configFileOption,
    logLevelOption,
    socketDirectoryOption,
    socketFileOption,
    baseDirectoryOption
};
rootCommand.SetAction((parserResult) =>
{
    bool updateOnlyValue = parserResult.GetValue(updateOnlyOption);
    FileInfo configFileValue = parserResult.GetValue(configFileOption) ?? new(Settings.DefaultConfigFile);
    NLog.LogLevel? logLevelValue = parserResult.GetValue(logLevelOption);
    DirectoryInfo? socketDirectoryValue = parserResult.GetValue(socketDirectoryOption);
    string? socketFileValue = parserResult.GetValue(socketFileOption);
    DirectoryInfo? baseDirectoryValue = parserResult.GetValue(baseDirectoryOption);

    if (updateOnlyValue)
    {
        // Log only minimal information in update-only mode
        logLevelValue ??= NLog.LogLevel.Error;
    }
    else
    {
        // Show startup message in regular mode
        Console.WriteLine($"Duet Control Server v{VersionHelper.GetVersion()}");
        Console.WriteLine("Written by Christian Hammacher for Duet3D");
        Console.WriteLine("Licensed under the terms of the GNU Public License Version 3");
        Console.WriteLine();
    }

    // Set up the host application
    IHost host;
    try
    {
        host = new HostBuilder()
            .UseNLog()
            .UseSystemd()
            .ConfigureAppConfiguration((hostingContext, config) =>
            {
                try
                {
                    config
                        .AddJsonFile(configFileValue.FullName, optional: true)
                        .AddCommandLine(args);
                }
                catch (JsonException je)
                {
                    Terminate(je, $"Failed to load settings: {je.Message}", ExitCode.Configuration);
                }
                catch (Exception e)
                {
                    Terminate(e, $"Failed to initialize settings: {e.Message}", ExitCode.Usage);
                }
            })
            .ConfigureServices((context, services) => services
                .AddSettings(context.Configuration, updateOnlyValue, logLevelValue, configFileValue, socketDirectoryValue, socketFileValue, baseDirectoryValue, out startErrorFile)
                .AddCodes()
                .AddCommands()
                .AddFiles()
                .AddIPC()
                .AddLink()
                .AddModel()
                .AddSPILink()
                .AddUtility()
            )
            .Build();
    }
    catch (Exception e)
    {
        Terminate(e, $"Failed to initialize environment: {e.Message}", ExitCode.OsError);
        return;
    }

    // Delete the startup error file when the application has been fully started
    host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() =>
    {
        if (File.Exists(startErrorFile))
        {
            try
            {
                File.Delete(startErrorFile);
            }
            catch (Exception e)
            {
                LogManager.GetCurrentClassLogger().Warn(e, "Failed to delete start error file {0}", startErrorFile);
            }
        }
    });

    // Run the host application
    host.Run();
    LogManager.Shutdown();
});

new CommandLineConfiguration(rootCommand).Invoke(args);

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
            StopPlugins stopCommand = commandFactory.Create<StopPlugins>();
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
            StopPlugins stopCommand = commandFactory.Create<StopPlugins>();
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