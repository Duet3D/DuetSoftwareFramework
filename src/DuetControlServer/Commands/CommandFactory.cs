using System;
using System.Collections.Concurrent;
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
    /// Cached activation factories per command type
    /// </summary>
    private static readonly ConcurrentDictionary<Type, ObjectFactory> _objectFactories = new();

    /// <summary>
    /// Get the cached activation factory for a command type, creating it on first use
    /// </summary>
    /// <param name="type">Command type</param>
    /// <returns>Activation factory</returns>
    private static ObjectFactory GetObjectFactory(Type type)
    {
        if (!_objectFactories.TryGetValue(type, out ObjectFactory? objectFactory))
        {
            objectFactory = ActivatorUtilities.CreateFactory(type, Type.EmptyTypes);
            _objectFactories[type] = objectFactory;
        }
        return objectFactory;
    }

    /// <summary>
    /// Create a new command instance
    /// </summary>
    /// <typeparam name="T">Command type</typeparam>
    /// <returns>Command instance</returns>
    public T Create<T>() where T : BaseCommand => (T)Create(typeof(T));

    /// <summary>
    /// Create a new command instance
    /// </summary>
    /// <param name="type">Command type</param>
    /// <returns>Command instance</returns>
    /// <exception cref="ArgumentException">Unsupported command</exception>
    public BaseCommand Create(Type type) => (BaseCommand)GetObjectFactory(type)(serviceProvider, null);

    /// <summary>
    /// Create a new command instance from the given JSON data
    /// </summary>
    /// <param name="commandName">Command name</param>
    /// <param name="commandData">Command data</param>
    /// <param name="supportedCommands">List of supported command types</param>
    /// <returns>Command instance</returns>
    /// <exception cref="ArgumentException">Unsupported command</exception>
    public BaseCommand Create(string commandName, JsonElement commandData, Type[] supportedCommands)
    {
        Type? commandType = supportedCommands.First(item => item.Name.Equals(commandName, StringComparison.InvariantCultureIgnoreCase))
                            ?? throw new ArgumentException($"Unsupported command {commandName}");

        BaseCommand command = (BaseCommand)GetObjectFactory(commandType)(serviceProvider, null);
        command.UpdateFromJson(commandData);
        return command;
    }

    /// <summary>
    /// Create a new command instance from the given JSON reader
    /// </summary>
    /// <param name="commandName">Command name</param>
    /// <param name="reader">JSON reader</param>
    /// <param name="supportedCommands">List of supported command types</param>
    /// <returns>Command instance</returns>
    /// <exception cref="ArgumentException">Unsupported command</exception>
    public BaseCommand Create(string commandName, ref Utf8JsonReader reader, Type[] supportedCommands)
    {
        Type? commandType = supportedCommands.First(item => item.Name.Equals(commandName, StringComparison.InvariantCultureIgnoreCase))
                            ?? throw new ArgumentException($"Unsupported command {commandName}");

        BaseCommand command = (BaseCommand)GetObjectFactory(commandType)(serviceProvider, null);
        command.UpdateFromJsonReader(ref reader);
        return command;
    }
}
