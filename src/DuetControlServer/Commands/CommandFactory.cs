using System;
using System.Linq;
using System.Text.Json;
using DuetAPI.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace DuetControlServer.Commands;

/// <summary>
/// Factory to create command instances
/// </summary>
/// <param name="serviceProvider">Service provider</param>
public class CommandFactory(IServiceProvider serviceProvider)
{
    /// <summary>
    /// Create a new command instance
    /// </summary>
    /// <typeparam name="T">Command type</typeparam>
    /// <returns>Command instance</returns>
    public T Create<T>() where T : BaseCommand => ActivatorUtilities.CreateInstance<T>(serviceProvider);

    /// <summary>
    /// Create a new command instance
    /// </summary>
    /// <param name="type">Command type</param>
    /// <returns>Command instance</returns>
    /// <exception cref="ArgumentException">Unsupported command</exception>
    public BaseCommand Create(Type type) => (BaseCommand)ActivatorUtilities.CreateInstance(serviceProvider, type);

    /// <summary>
    /// Create a new command instance from the given JSON data
    /// </summary>
    /// <param name="commandName">Command name</param>
    /// <param name="commandData">Command data</param>
    /// <returns>Command instance</returns>
    /// <exception cref="ArgumentException">Unsupported command</exception>
    public BaseCommand Create(string commandName, JsonElement commandData, Type[] supportedCommands)
    {
        Type? commandType = supportedCommands.First(item => item.Name.Equals(commandName, StringComparison.InvariantCultureIgnoreCase))
                            ?? throw new ArgumentException($"Unsupported command {commandName}");

        BaseCommand command = (BaseCommand)ActivatorUtilities.CreateInstance(serviceProvider, commandType);
        command.UpdateFromJson(commandData);
        return command;
    }

    /// <summary>
    /// Create a new command instance from the given JSON reader
    /// </summary>
    /// <param name="commandName">Command name</param>
    /// <param name="commandData">Command data</param>
    /// <returns>Command instance</returns>
    /// <exception cref="ArgumentException">Unsupported command</exception>
    public BaseCommand Create(string commandName, ref Utf8JsonReader reader, Type[] supportedCommands)
    {
        Type? commandType = supportedCommands.First(item => item.Name.Equals(commandName, StringComparison.InvariantCultureIgnoreCase))
                            ?? throw new ArgumentException($"Unsupported command {commandName}");

        BaseCommand command = (BaseCommand)ActivatorUtilities.CreateInstance(serviceProvider, commandType);
        command.UpdateFromJsonReader(ref reader);
        return command;
    }
}

