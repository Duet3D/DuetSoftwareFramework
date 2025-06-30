using DuetPluginService;
using DuetPluginService.Commands;
using DuetPluginService.IPC;
using DuetPluginService.PermissionManagers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Hosting;
using System.CommandLine.NamingConventionBinder;
using System.CommandLine.Parsing;

var configOption = new Option<string>(
    [ "-c", "--config" ],
    description: "Path to the configuration file",
    getDefaultValue: () => Settings.DefaultConfigFile);

var rootCommand = new RootCommand("Duet Plugin Service")
{
    configOption
};
rootCommand.Handler = CommandHandler.Create<IHost>(async (host) =>
{
    await host.WaitForShutdownAsync();
});

string configFile = Settings.DefaultConfigFile;
return await new CommandLineBuilder(rootCommand)
    .AddMiddleware((context) =>
    {
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
            .Configure<Settings>(context.Configuration)
            .AddSingleton<CommandFactory>()
            .AddSingleton<IPermissionManager, AppArmorPermissionManager>()
            .AddSingleton<PluginStore>()
            .AddSingleton<PluginServiceConnection>()
            .AddHostedService<CommandService>()
        )
    )
    .UseDefaults()
    .Build()
    .InvokeAsync(args);
