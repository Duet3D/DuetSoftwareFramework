using System;
using System.Threading;
using System.Threading.Tasks;
using DuetControlServer.Model;
using Microsoft.Extensions.Options;

namespace DuetControlServer.IPC;

/// <summary>
/// Class to manage read/write locks of third-party plugins
/// </summary>
/// <param name="model">Object model</param>
/// <param name="settings">Settings</param>
public class LockManager(ObjectModel model, IOptions<Settings> settings)
{
    /// <summary>
    /// Connection that acquired the current lock
    /// </summary>
    private Connection? _lockConnection;

    /// <summary>
    /// Indicates if a third-party application has locked the object model for writing
    /// </summary>
    public bool IsLocked => _lockConnection is not null;

    /// <summary>
    /// Read/write lock held by a third-party plugins
    /// </summary>
    private IDisposable? _lock;

    /// <summary>
    /// Function to create a read/write lock to the object model
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task LockMachineModel(Connection connection, CancellationToken cancellationToken = default)
    {
        _lock = await model.AccessReadWriteAsync(cancellationToken);
        _lockConnection = connection;
    }

    /// <summary>
    /// Unlock the machine model again
    /// </summary>
    public async Task UnlockMachineModel(Connection connection, CancellationToken cancellationToken = default)
    {
        if (_lockConnection == connection)
        {
            _lockConnection = null;
            _lock?.Dispose();
            _lock = null;

            if (settings.Value.NoSpi)
            {
                // Make sure functions waiting for full model updates don't stall
                await model.FullyUpdatedAsync(cancellationToken);
            }
        }
    }
}
