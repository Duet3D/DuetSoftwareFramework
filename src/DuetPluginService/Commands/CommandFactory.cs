using System;
using System.Linq;
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
    /// List of supported commands in this mode
    /// </summary>
    public static readonly Type[] SupportedCommands =
    [
        typeof(IPC.InstallPlugin),
        typeof(IPC.ReloadPlugin),
        typeof(IPC.StartPlugin),
        typeof(IPC.StopPlugin),
        typeof(IPC.UninstallPlugin),
        typeof(IPC.InstallSystemPackage),
        typeof(IPC.UninstallSystemPackage),
    ];

    /// <summary>
    /// Create a new command instance
    /// </summary>
    /// <typeparam name="T">Command type</typeparam>
    /// <returns>Command instance</returns>
    public T Create<T>() where T : BaseCommand
    {
        if (!SupportedCommands.Contains(typeof(T)))
        {
            throw new ArgumentException($"Unsupported command {typeof(T).Name}");
        }
        return ActivatorUtilities.CreateInstance<T>(serviceProvider);
    }

    /// <summary>
    /// Create a new command instance
    /// </summary>
    /// <param name="type">Command type</param>
    /// <returns>Command instance</returns>
    /// <exception cref="ArgumentException">Unsupported command</exception>
    public BaseCommand Create(Type type)
    {
        if (!SupportedCommands.Contains(type))
        {
            throw new ArgumentException($"Unsupported command {type.Name}");
        }
        return (BaseCommand)ActivatorUtilities.CreateInstance(serviceProvider, type);
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
        Type? commandType = SupportedCommands.First(item => item.Name.Equals(commandName, StringComparison.InvariantCultureIgnoreCase))
                            ?? throw new ArgumentException($"Unsupported command {commandName}");

        BaseCommand command = (BaseCommand)ActivatorUtilities.CreateInstance(serviceProvider, commandType);
        command.UpdateFromJson(commandData);
        return command;
    }
}
