using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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
    /// Create a new command instance
    /// </summary>
    /// <typeparam name="T">Command type</typeparam>
    /// <returns>Command instance</returns>
    public T Create<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : BaseCommand => (T)Create(typeof(T));

    /// <summary>
    /// Create a new command instance
    /// </summary>
    /// <param name="type">Command type</param>
    /// <returns>Command instance</returns>
    public BaseCommand Create([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type type)
    {
        // The factory cannot be created in a GetOrAdd callback because trimmer annotations do not flow into it
        if (!_objectFactories.TryGetValue(type, out ObjectFactory? objectFactory))
        {
            objectFactory = ActivatorUtilities.CreateFactory(type, Type.EmptyTypes);
            _objectFactories[type] = objectFactory;
        }
        return (BaseCommand)objectFactory(serviceProvider, null);
    }
}
