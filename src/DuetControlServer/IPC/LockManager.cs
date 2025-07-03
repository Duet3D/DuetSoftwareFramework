using System;
using System.Threading;
using System.Threading.Tasks;
using DuetControlServer.Model;

namespace DuetControlServer.IPC;

/// <summary>
/// Class to manage read/write locks of third-party plugins
/// </summary>
/// <param name="model">Object model</param>
public class LockManager(ObjectModel model)
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
    /// <param name="connection">Connection that is locking the model</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    public void LockMachineModel(Connection connection, CancellationToken cancellationToken = default)
    {
        _lock = model.AccessReadWrite(cancellationToken);
        _lockConnection = connection;
    }

    /// <summary>
    /// Function to create a read/write lock to the object model
    /// </summary>
    /// <param name="connection">Connection that is locking the model</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task LockMachineModelAsync(Connection connection, CancellationToken cancellationToken = default)
    {
        _lock = await model.AccessReadWriteAsync(cancellationToken);
        _lockConnection = connection;
    }

    /// <summary>
    /// Unlock the machine model again
    /// </summary>
    /// <param name="connection">Connection that is unlocking the model</param>
    public void UnlockMachineModel(Connection connection)
    {
        if (_lockConnection == connection)
        {
            _lockConnection = null;
            _lock?.Dispose();
            _lock = null;
        }
    }
}
