using System;
using System.Threading;
using System.Threading.Tasks;

namespace DuetSharedLibrary;

/// <summary>
/// Mutual exclusion lock that can be held across await points
/// </summary>
/// <remarks>
/// Uncontended acquisitions do not allocate, which matters because some of these locks are taken per G-code.
/// Both <see cref="Lock"/> and <see cref="LockAsync"/> queue on the same waiter list, so waiters are served in FIFO order
/// </remarks>
public sealed class AsyncLock
{
    /// <summary>
    /// Underlying semaphore. The maximum count is 1, so releasing a lock twice throws instead of corrupting the count
    /// </summary>
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    /// Disposable representing an acquired lock
    /// </summary>
    /// <param name="semaphore">Semaphore to release again</param>
    /// <remarks>
    /// This must be obtained from <see cref="Lock"/> or <see cref="LockAsync"/> and disposed exactly once
    /// </remarks>
    public readonly struct Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        /// <summary>
        /// Release the lock again
        /// </summary>
        public void Dispose() => semaphore.Release();
    }

    /// <summary>
    /// Acquire this lock
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Disposable lock to be used with a using directive</returns>
    /// <exception cref="OperationCanceledException">Lock has not been acquired</exception>
    /// <remarks>
    /// This blocks the calling thread. It waits on the asynchronous path so that synchronous
    /// and asynchronous waiters share a single queue, else the two could overtake each other
    /// </remarks>
    public Releaser Lock(CancellationToken cancellationToken = default)
    {
        _semaphore.WaitAsync(cancellationToken).GetAwaiter().GetResult();
        return new Releaser(_semaphore);
    }

    /// <summary>
    /// Acquire this lock asynchronously
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Disposable lock to be used with a using directive</returns>
    /// <exception cref="OperationCanceledException">Lock has not been acquired</exception>
    public async ValueTask<Releaser> LockAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        return new Releaser(_semaphore);
    }
}
