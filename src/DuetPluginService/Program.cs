using DuetPluginService;
using DuetPluginService.Commands;
using DuetPluginService.IPC;
using DuetPluginService.PermissionManagers;
using DuetSharedLibrary;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using System.Collections.Generic;
using System.CommandLine;

Option<string> configOption = new("-c", "--config")
{
    Description = "Path to the configuration file",
    DefaultValueFactory = _ => Settings.DefaultConfigFile
};
Option<string> logLevelOption = new("--log-level", "-l")
{
    Description = "Set the log level for the application (trace, debug, info/information, warn/warning, error, fatal/critical, off/none)"
};

RootCommand rootCommand = new("Duet Plugin Service")
{
    configOption,
    logLevelOption
};

rootCommand.SetAction((parserResult) =>
{
    string configValue = parserResult.GetValue(configOption) ?? Settings.DefaultConfigFile;
    string? logLevelString = parserResult.GetValue(logLevelOption);
    LogLevel? logLevelValue = (logLevelString != null) ? LogLevelHelper.ParseLogLevel(logLevelString) : null;

    Settings? capturedSettings = null;

    IHost host = Host.CreateDefaultBuilder()
        .ConfigureLogging((context, logging) =>
        {
            logging.ClearProviders();

            // Get the log level from command line parameter first, then from configuration
            LogLevel logLevel;
            if (logLevelValue.HasValue)
            {
                logLevel = logLevelValue.Value;
            }
            else
            {
                string configLogLevelString = context.Configuration.GetValue<string>("LogLevel") ?? "Information";
                logLevel = LogLevelHelper.ParseLogLevel(configLogLevelString);
            }

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
            config
                .AddJsonFile(configValue, optional: true)
                .AddCommandLine([.. parserResult.UnmatchedTokens]);
        })
        .ConfigureServices((context, services) =>
        {
            // Ensure systemd console logging uses our custom formatter (must be after UseSystemd)
            services.Configure<ConsoleLoggerOptions>(options =>
            {
                options.FormatterName = nameof(CommonLogFormatter);
            });

            // Exclude LogLevel from the configuration before binding to Settings:
            // the standard binder uses Enum.Parse which rejects short aliases like 'info'
            var configData = new Dictionary<string, string?>();
            foreach (var kvp in context.Configuration.AsEnumerable())
            {
                if (kvp.Key != "LogLevel" && kvp.Value != null)
                {
                    configData[kvp.Key] = kvp.Value;
                }
            }
            var filteredConfig = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

            services
                .Configure<Settings>(filteredConfig)
                .AddSingleton<CommandFactory>()
                .AddSingleton<IPermissionManager, AppArmorPermissionManager>()
                .AddSingleton<PluginStore>()
                .AddHostedService<PluginService>()
                .AddSingleton<PluginServiceConnection>()
                .AddHostedService<CommandService>();
        })
        .Build();

    capturedSettings = host.Services.GetRequiredService<IOptions<Settings>>().Value;
    if (logLevelValue.HasValue)
    {
        capturedSettings.LogLevel = logLevelValue.Value;
    }
    else
    {
        // Re-apply using LogLevelHelper to support NLog-style aliases (info, warn, fatal, etc.)
        // The standard options binder uses Enum.Parse which doesn't recognise short names
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        string configLogLevelString = configuration.GetValue<string>("LogLevel") ?? "Information";
        capturedSettings.LogLevel = LogLevelHelper.ParseLogLevel(configLogLevelString);
    }
    host.Run();
});

return rootCommand.Parse(args).Invoke();
