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
Option<LogLevel> logLevelOption = new("--log-level", "-l")
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
    LogLevel? logLevelValue = parserResult.GetValue(logLevelOption);
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
    try
    {
        host = Host.CreateDefaultBuilder()
            .UseNLog()
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
                LogManager.GetCurrentClassLogger().Warn(e, "Failed to delete start error file {File}", startErrorFile);
            }
        }
    });

    // Run the host application
    host.Run();
    LogManager.Shutdown();
});

new CommandLineConfiguration(rootCommand).Invoke(args);
