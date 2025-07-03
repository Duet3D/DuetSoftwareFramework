using System;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace DuetControlServer.Codes;

/// <summary>
/// Factory to create code instances
/// </summary>
/// <remarks>
/// This class is specialized for code instances.
/// It may be further enhanced to reuse of existing instances.
/// </remarks>
/// <param name="serviceProvider">Service provider</param>
public sealed class CodeFactory(IServiceProvider serviceProvider)
{
    /// <summary>
    /// Create a new code instance
    /// </summary>
    /// <returns>Code instance</returns>
    public Commands.Code Create() => ActivatorUtilities.CreateInstance<Commands.Code>(serviceProvider);

    /// <summary>
    /// Create a new code instance
    /// </summary>
    /// <returns>Code instance</returns>
    public Commands.Code Create(string code) => ActivatorUtilities.CreateInstance<Commands.Code>(serviceProvider, code);

    /// <summary>
    /// Create a new code instance
    /// </summary>
    /// <param name="json">Code data</param>
    /// <returns>Code instance</returns>
    /// <exception cref="ArgumentException">Unsupported command</exception>
    public Commands.Code Create(JsonElement json)
    {
        Commands.Code command = ActivatorUtilities.CreateInstance<Commands.Code>(serviceProvider);
        command.UpdateFromJson(json);
        return command;
    }
}
