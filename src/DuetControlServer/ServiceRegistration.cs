using DuetControlServer.Codes;
using DuetControlServer.Commands;
using DuetControlServer.Events;
using DuetControlServer.Fans;
using DuetControlServer.Files;
using DuetControlServer.Heat;
using DuetControlServer.IPC;
using DuetControlServer.Link;
using DuetControlServer.Model;
using DuetControlServer.Motion;
using DuetControlServer.Ports;
using DuetControlServer.Spindles;
using DuetControlServer.Tools;
using DuetControlServer.Utility;
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DuetControlServer;

/// <summary>
/// Registration of every DuetControlServer component, shared by the daemon and the hosts that run it
/// in-process for tests
/// </summary>
public static class ServiceRegistration
{
    /// <summary>Register every component except the settings, which each host provides its own way</summary>
    /// <remarks>
    /// Every delay, timeout and clock reading goes through the registered <see cref="TimeProvider"/>,
    /// which is the system clock unless the host registered its own first
    /// </remarks>
    public static IServiceCollection AddDuetControlServer(this IServiceCollection services) => services
        .AddTimeProvider()
        .AddCodes()
        .AddCommands()
        .AddEvents()
        .AddFiles()
        .AddIPC()
        .AddLink()
        .AddModel()
        .AddLinkAdapter()
        .AddMotion()
        .AddPorts()
        .AddSpindles()
        .AddFans()
        .AddHeat()
        .AddTools()
        .AddUtility();

    private static IServiceCollection AddTimeProvider(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
