using System;
using System.Threading;
using System.Threading.Tasks;

namespace DuetAPI.Commands;

/// <summary>
/// Base class of commands that do not return a result
/// </summary>
public abstract class Command : BaseCommand
{
    /// <summary>
    /// Reserved for the actual command implementation in the control server
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public virtual Task ExecuteAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException($"{Command} not implemented");

    /// <summary>
    /// Invokes the command implementation
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>null</returns>
    public override async Task<object?> InvokeAsync(CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(cancellationToken);
        return null;
    }
}

/// <summary>
/// Base class of a command that returns a result
/// </summary>
/// <typeparam name="T">Type of the command result</typeparam>
public abstract class Command<T> : BaseCommand
{
    /// <summary>
    /// Reserved for the actual command implementation in the control server
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Command result</returns>
    public virtual Task<T> ExecuteAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException($"{Command}<{nameof(T)}> not implemented");

    /// <summary>
    /// Invokes the command implementation
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Command result</returns>
    public override async Task<object?> InvokeAsync(CancellationToken cancellationToken = default) => await ExecuteAsync(cancellationToken);
}