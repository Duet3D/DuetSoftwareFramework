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
using Microsoft.Extensions.DependencyInjection;

namespace DuetControlServer;

/// <summary>
/// Registration of every DuetControlServer component, shared by the daemon and the hosts that run it
/// in-process for tests
/// </summary>
public static class ServiceRegistration
{
    /// <summary>Register every component except the settings, which each host provides its own way</summary>
    public static IServiceCollection AddDuetControlServer(this IServiceCollection services) => services
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
}
