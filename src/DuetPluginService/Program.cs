using DuetPluginService;
using DuetPluginService.Services;
using DuetPluginService.Singletons;
using DuetPluginService.Singletons.PermissionManagers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;

Console.WriteLine($"Duet Plugin Service v{Utility.Version}");
Console.WriteLine("Written by Christian Hammacher for Duet3D");
Console.WriteLine("Licensed under the terms of the GNU Public License Version 3");
Console.WriteLine();

IHost host = Host.CreateDefaultBuilder()
    .UseSystemd()
    .ConfigureAppConfiguration((hostingContext, config) =>
    {
        string configFile = Settings.DefaultConfigFile;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "-c" or "--config")
            {
                configFile = args[i + 1];
                break;
            }
        }

        config
            .AddJsonFile(configFile, true)
            .AddCommandLine(args);
    })
    .ConfigureServices((context, services) => services
        .Configure<Settings>(context.Configuration)
        .AddSingleton<CommandActivator>()
        .AddSingleton<IPermissionManager, AppArmorPermissionManager>()
        .AddSingleton<PluginStore>()
        .AddSingleton<PluginServiceConnection>()
        .AddHostedService<CommandService>()
    )
    .Build();

await host.RunAsync();
