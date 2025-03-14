using DuetPluginService;
using DuetPluginService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
            if (args[i] == "--config")
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
        .AddSingleton<PluginManager>()
        .AddHostedService<PluginServiceConnection>()
        .AddHostedService<ControlService>()
    )
    .Build();

var settings = host.Services.GetRequiredService<IOptions<Settings>>().Value;
Console.WriteLine("config path: {0}", settings.ConfigFilename);

await host.RunAsync();
