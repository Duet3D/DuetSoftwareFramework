using DuetPluginService;
using DuetPluginService.Commands;
using DuetPluginService.IPC;
using DuetPluginService.PermissionManagers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.CommandLine;

Option<string> configOption = new("-c", "--config")
{
    Description = "Path to the configuration file",
    DefaultValueFactory = _ => Settings.DefaultConfigFile
};

RootCommand rootCommand = new("Duet Plugin Service")
{
    configOption
};

rootCommand.SetAction((parserResult) =>
{
    string configValue = parserResult.GetValue(configOption) ?? Settings.DefaultConfigFile;
    Host.CreateDefaultBuilder()
        .UseSystemd()
        .ConfigureAppConfiguration((hostingContext, config) =>
        {
            config
                .AddJsonFile(configValue, optional: true)
                .AddCommandLine([.. parserResult.UnmatchedTokens]);
        })
        .ConfigureServices((context, services) =>
            services
                .Configure<Settings>(context.Configuration)
                .AddSingleton<CommandFactory>()
                .AddSingleton<IPermissionManager, AppArmorPermissionManager>()
                .AddSingleton<PluginStore>()
                .AddHostedService<PluginService>()
                .AddSingleton<PluginServiceConnection>()
                .AddHostedService<CommandService>()
        )
        .Build()
        .Run();
});

return rootCommand.Parse(args).Invoke();
