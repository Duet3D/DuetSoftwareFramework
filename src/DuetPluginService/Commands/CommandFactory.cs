using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using DuetAPI.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace DuetPluginService.Commands;

/// <summary>
/// Factory to create command instances
/// </summary>
/// <param name="serviceProvider">Service provider</param>
public class CommandFactory(IServiceProvider serviceProvider)
{
    /// <summary>
    /// Command supported in this mode, paired with a factory that creates it
    /// </summary>
    /// <param name="Name">Name of the command</param>
    /// <param name="Type">Type of the command</param>
    /// <param name="Create">Factory to create a new instance of the command</param>
    private sealed record SupportedCommand(string Name, Type Type, Func<CommandFactory, BaseCommand> Create)
    {
        public static SupportedCommand For<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : BaseCommand
            => new(typeof(T).Name, typeof(T), factory => factory.Create<T>());
    }

    /// <summary>
    /// List of supported commands in this mode
    /// </summary>
    private static readonly SupportedCommand[] _supportedCommands =
    [
        SupportedCommand.For<IPC.InstallPlugin>(),
        SupportedCommand.For<IPC.ReloadPlugin>(),
        SupportedCommand.For<IPC.ResolvePluginProcess>(),
        SupportedCommand.For<IPC.StartPlugin>(),
        SupportedCommand.For<IPC.StopPlugin>(),
        SupportedCommand.For<IPC.UninstallPlugin>(),
        SupportedCommand.For<IPC.InstallSystemPackage>(),
        SupportedCommand.For<IPC.UninstallSystemPackage>()
    ];

    /// <summary>
    /// Create a new command instance
    /// </summary>
    /// <typeparam name="T">Command type</typeparam>
    /// <returns>Command instance</returns>
    /// <exception cref="ArgumentException">Unsupported command</exception>
    public T Create<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : BaseCommand
    {
        foreach (SupportedCommand supportedCommand in _supportedCommands)
        {
            if (supportedCommand.Type == typeof(T))
            {
                return ActivatorUtilities.CreateInstance<T>(serviceProvider);
            }
        }
        throw new ArgumentException($"Unsupported command {typeof(T).Name}");
    }

    /// <summary>
    /// Create a new command instance
    /// </summary>
    /// <param name="commandName">Command name</param>
    /// <param name="commandData">Command data</param>
    /// <returns>Command instance</returns>
    /// <exception cref="ArgumentException">Unsupported command</exception>
    public BaseCommand Create(string commandName, JsonElement commandData)
    {
        foreach (SupportedCommand supportedCommand in _supportedCommands)
        {
            if (supportedCommand.Name.Equals(commandName, StringComparison.InvariantCultureIgnoreCase))
            {
                BaseCommand command = supportedCommand.Create(this);
                command.UpdateFromJson(commandData);
                return command;
            }
        }
        throw new ArgumentException($"Unsupported command {commandName}");
    }
}
