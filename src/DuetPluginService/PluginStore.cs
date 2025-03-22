using DuetAPI.ObjectModel;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace DuetPluginService;

/// <summary>
/// Main storage class for registered plugins
/// </summary>
public class PluginStore
{
    private readonly AsyncLock _lock = new();

    /// <summary>
    /// Lock access to the plugins
    /// </summary>
    /// <returns>Lock instance</returns>
    public IDisposable Lock() => _lock.Lock();

    /// <summary>
    /// Lock access to the plugins asynchronously
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Lock instance</returns>
    public AwaitableDisposable<IDisposable> LockAsync(CancellationToken cancellationToken) => _lock.LockAsync(cancellationToken);

    /// <summary>
    /// List of plugins
    /// </summary>
    public List<Plugin> Plugins { get; } = [];

    /// <summary>
    /// Plugin IDs vs processes
    /// </summary>
    public Dictionary<string, Process> Processes { get; } = [];
}
