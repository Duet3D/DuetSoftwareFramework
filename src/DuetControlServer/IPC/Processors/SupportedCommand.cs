using System;
using System.Diagnostics.CodeAnalysis;
using DuetAPI.Commands;
using DuetControlServer.Commands;

namespace DuetControlServer.IPC.Processors;

/// <summary>
/// Command supported by an IPC processor
/// </summary>
/// <param name="Name">Name of the command</param>
/// <param name="Type">Type of the command</param>
/// <param name="Create">Factory to create a new instance of the command</param>
public sealed record SupportedCommand(string Name, Type Type, Func<CommandFactory, BaseCommand> Create)
{
    /// <summary>
    /// Create a descriptor for the given command type
    /// </summary>
    /// <typeparam name="T">Command type</typeparam>
    /// <returns>Supported command descriptor</returns>
    public static SupportedCommand For<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : BaseCommand
        => new(typeof(T).Name, typeof(T), factory => factory.Create<T>());

    /// <summary>
    /// Check if the given command type is part of the given list of supported commands
    /// </summary>
    /// <param name="commands">List of supported commands</param>
    /// <param name="commandType">Command type to look for</param>
    /// <returns>True if the command type is supported</returns>
    public static bool IsSupported(SupportedCommand[] commands, Type commandType)
    {
        foreach (SupportedCommand command in commands)
        {
            if (command.Type == commandType)
            {
                return true;
            }
        }
        return false;
    }
}
