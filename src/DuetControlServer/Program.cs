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
using System;
using System.CommandLine;
using System.IO;
using System.Text.Json;

string? startErrorFile = Defaults.StartErrorFile;

/// <summary>
/// Parse a log level string, handling NLog level names and shorter versions
/// </summary>
/// <param name="logLevelString">The log level string to parse</param>
/// <returns>The parsed LogLevel</returns>
LogLevel ParseLogLevel(string logLevelString)
{
    return logLevelString.ToLowerInvariant() switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "info" or "information" => LogLevel.Information,
        "warn" or "warning" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "fatal" or "critical" => LogLevel.Critical,
        "off" or "none" => LogLevel.None,
        _ => LogLevel.Information
    };
}

/// <summary>
/// Print the reason for the start error, write it to the start error file, and exit this application
/// </summary>
/// <param name="e">Exception that caused the termination</param>
/// <param name="reason">Reason for the program termination</param>
/// <param name="exitCode">Exit code</param>
/// <param name="loggerFactory">Optional logger factory for logging</param>
void Terminate(Exception e, string reason, int exitCode, ILoggerFactory? loggerFactory = null)
{
    if (loggerFactory != null)
    {
        loggerFactory.CreateLogger("DuetControlServer").LogCritical(e, reason);
    }
    else
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
    Description = "Full path to the IPC socket file",
    DefaultValueFactory = _ => Defaults.FullSocketPath
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
rootCommand.SetAction((parserResult) =>
{
    bool updateOnlyValue = parserResult.GetValue(updateOnlyOption);
    FileInfo configFileValue = parserResult.GetValue(configFileOption) ?? new(Settings.DefaultConfigFile);
    string? logLevelString = parserResult.GetValue(logLevelOption);
    LogLevel? logLevelValue = logLevelString != null ? ParseLogLevel(logLevelString) : null;
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
        // Show startup message in regular mode
        Console.WriteLine($"Duet Control Server v{VersionHelper.GetVersion()}");
        Console.WriteLine("Written by Christian Hammacher for Duet3D");
        Console.WriteLine("Licensed under the terms of the GNU Public License Version 3");
        Console.WriteLine();
    }

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
                    logLevel = ParseLogLevel(configLogLevelString);
                }
                
                // Add console logging with custom formatter
                logging.AddConsole(options =>
                {
                    options.FormatterName = nameof(CommonLogFormatter);
                })
                .AddConsoleFormatter<CommonLogFormatter, CommonLogFormatterOptions>()
                .SetMinimumLevel(logLevel);
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
                    .AddSPILink()
                    .AddUtility();
            })
            .Build();
        
        // Capture logger factory for error logging
        loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
    }
    catch (Exception e)
    {
        Terminate(e, $"Failed to initialize environment: {e.Message}", ExitCode.OsError, loggerFactory);
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
                host.Services.GetRequiredService<ILogger<Program>>().LogWarning(e, "Failed to delete start error file {File}", startErrorFile);
            }
        }
    });

    // Run the host application
    host.Run();
});

return rootCommand.Parse(args).Invoke();
