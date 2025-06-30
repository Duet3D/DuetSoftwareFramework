using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.Connection;

namespace DuetControlServer.IPC.Processors;

/// <summary>
/// Interface for connection interpreters
/// </summary>
/// <seealso cref="ConnectionMode"/>
public interface IProcessor
{
    /// <summary>
    /// List of supported command types
    /// </summary>
    public static Type[] SupportedCommands { get; } = [];

    /// <summary>
    /// Connection to the IPC client served by this processor
    /// </summary>
    public Connection Connection { get; }

    /// <summary>
    /// Worker method for a given connection.
    /// No <see cref="CancellationToken"/> is passed here, use <see cref="Program.CancellationToken"/> instead.
    /// Once this task exits the connection is terminated.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the worker</param>
    /// <returns>Task that represents the worker lifecycle</returns>
    /// <exception cref="NotImplementedException">Thrown if this method is not overridden</exception>
    public Task ProcessAsync(CancellationToken cancellationToken);
}
