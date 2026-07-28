using System;
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
}
