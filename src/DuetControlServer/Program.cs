using DuetAPI.Connection;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.CommandLine;
using System.IO;
using System.Runtime;
using System.Text.Json;

string? startErrorFile = Defaults.StartErrorFile;

// Print the reason for the start error, write it to the start error file, and exit this application
void Terminate(Exception e, string reason, int exitCode, ILoggerFactory? loggerFactory = null)
{
    bool logged = false;
    if (loggerFactory != null)
    {
        try
        {
            loggerFactory.CreateLogger("DuetControlServer").LogCritical(e, reason);
            logged = true;
        }
        catch (ObjectDisposedException)
        {
            // host.Run() disposes the logger factory as part of its shutdown, so a startup
            // failure surfacing afterwards hits an already-disposed factory; fall back to the console
        }
    }
    if (!logged)
    {
        Console.Error.WriteLine($"[fatal] {reason}");
        Console.Error.WriteLine($"   {e}");
    }
    File.WriteAllText(startErrorFile, reason);
    Environment.Exit(exitCode);
}

Option<bool> updateOnlyOption = new("--update", "-u")
{
    Description = "Update RepRapFirmware and exit. This works even if another instance is already started"
};
Option<string> logLevelOption = new("--log-level", "-l")
{
    Description = "Set the log level for the application (trace, debug, info/information, warn/warning, error, fatal/critical, off/none)"
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
    Description = "Full path to the IPC socket file"
};
Option<DirectoryInfo> baseDirectoryOption = new("--base-directory", "-b")
{
    Description = "Base directory for the emulated SD card (0:/ on Duet controllers)"
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
rootCommand.SetAction(async (parserResult) =>
{
    bool updateOnlyValue = parserResult.GetValue(updateOnlyOption);
    FileInfo configFileValue = parserResult.GetValue(configFileOption) ?? new(Settings.DefaultConfigFile);
    string? logLevelString = parserResult.GetValue(logLevelOption);
    LogLevel? logLevelValue = (logLevelString != null) ? LogLevelHelper.ParseLogLevel(logLevelString) : null;
    DirectoryInfo? socketDirectoryValue = parserResult.GetValue(socketDirectoryOption);
    string? socketFileValue = parserResult.GetValue(socketFileOption);
    DirectoryInfo? baseDirectoryValue = parserResult.GetValue(baseDirectoryOption);

    if (updateOnlyValue)
    {
        // Log only minimal information in update-only mode
        logLevelValue ??= LogLevel.Error;
    }
    else
    {
        // Trade higher steady-state memory usage for fewer blocking gen2 collections, else full GC
        // pauses may stall time-critical data exchanges with the Duet and IPC message processing
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        // Show startup message in regular mode
        Console.WriteLine($"Duet Control Server v{VersionHelper.GetVersion()}");
        Console.WriteLine("Written by Christian Hammacher for Duet3D");
        Console.WriteLine("Licensed under the terms of the GNU Public License Version 3");
        Console.WriteLine();
    }

    // Resolved from the DI container after host.Build(); the logging filter below reads
    // Settings.LogLevel on every IsEnabled() call, so M111 P-1 S"level" takes effect immediately.
    Settings? capturedSettings = null;

    // Set up the host application
    IHost host;
    ILoggerFactory? loggerFactory = null;
    try
    {
        host = Host.CreateDefaultBuilder()
            .ConfigureLogging((context, logging) =>
            {
                // Clear default logging providers
                logging.ClearProviders();
                
                // Get the log level from command line parameter first, then from configuration
                LogLevel logLevel;
                if (logLevelValue.HasValue)
                {
                    logLevel = logLevelValue.Value;
                }
                else
                {
                    // Get the log level from configuration, handling NLog level names and shorter versions
                    string configLogLevelString = context.Configuration.GetValue<string>("LogLevel") ?? "Information";
                    logLevel = LogLevelHelper.ParseLogLevel(configLogLevelString);
                }

                // Add console logging with custom formatter.
                // The floor is Trace so the dynamic filter below can lower the effective level
                // at runtime (e.g. via M111 P-1 S"debug") without a restart.
                // capturedSettings is null during very early startup, so fall back to the
                // configured initial level until the host is built and the DI container resolves it.
                logging.AddConsole(options =>
                {
                    options.FormatterName = nameof(CommonLogFormatter);
                })
                .AddConsoleFormatter<CommonLogFormatter, CommonLogFormatterOptions>()
                .SetMinimumLevel(LogLevel.Trace)
                .AddFilter((_, level) => level >= (capturedSettings?.LogLevel ?? logLevel));
            })
            .UseSystemd()
            .ConfigureAppConfiguration((hostingContext, config) =>
            {
                try
                {
                    config
                        .AddJsonFile(configFileValue.FullName, optional: true)
                        .AddCommandLine([.. parserResult.UnmatchedTokens]);
                }
                catch (JsonException je)
                {
                    Terminate(je, $"Failed to load settings: {je.Message}", ExitCode.Configuration, loggerFactory);
                }
                catch (Exception e)
                {
                    Terminate(e, $"Failed to initialize settings: {e.Message}", ExitCode.Usage, loggerFactory);
                }
            })
            .ConfigureServices((context, services) =>
            {
                // Ensure systemd console logging uses our custom formatter (must be after UseSystemd)
                services.Configure<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>(options =>
                {
                    options.FormatterName = nameof(CommonLogFormatter);
                });

                services
                    .AddSettings(context.Configuration, updateOnlyValue, logLevelValue, configFileValue, socketDirectoryValue, socketFileValue, baseDirectoryValue, out startErrorFile)
                    .AddCodes()
                    .AddCommands()
                    .AddFiles()
                    .AddIPC()
                    .AddLink()
                    .AddModel()
                    .AddLinkAdapter()
                    .AddUtility();
            })
            .Build();

        // Capture logger factory and settings for error logging and dynamic log-level filtering
        loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
        capturedSettings = host.Services.GetRequiredService<IOptions<Settings>>().Value;
        DuetAPI.Utility.JsonHelper.ReceiveBufferSize = capturedSettings.IpcJsonBufferSize;
    }
    catch (Exception e)
    {
        Terminate(e, $"Failed to initialize environment: {e.Message}", ExitCode.OsError, loggerFactory);
        return;
    }

    // Check if the firmware is supposed to be updated only
    if (updateOnlyValue)
    {
        try
        {
            var firmwareUpdater = host.Services.GetRequiredService<FirmwareUpdater>();
            var cancellationToken = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped;
            if (await firmwareUpdater.TryRemoteFirmwareUpdateAsync(cancellationToken))
            {
                Environment.Exit(ExitCode.Success);
                return;
            }
        }
        catch (Exception e)
        {
            Terminate(e, $"Failed to update firmware remotely: {e.Message}", ExitCode.IoError, loggerFactory);
        }
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
                host.Services.GetRequiredService<ILogger<Program>>().LogWarning(e, "Failed to delete start error file {File}", startErrorFile);
            }
        }
    });

    // Run the host application. Startup failures of hosted services (e.g. IPC socket binding or
    // SPI/USB device initialization) surface here and must produce the start error file too
    try
    {
        host.Run();
    }
    catch (Exception e)
    {
        Terminate(e, $"Failed to start: {e.Message}", ExitCode.OsError, loggerFactory);
    }
});

return rootCommand.Parse(args).Invoke();
