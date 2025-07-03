using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DuetControlServer.Model;

/// <summary>
/// Wrapper around the lock which notifies subscribers whenever an update has been processed.
/// It is also able to detect the origin of model-related deadlocks
/// </summary>
public sealed class LockWrapper : IDisposable
{
    /// <summary>
    /// Internal lock
    /// </summary>
    private readonly IDisposable _lock;

    /// <summary>
    /// Indicates if this lock is meant for write access
    /// </summary>
    private readonly bool _isWriteLock;

    /// <summary>
    /// Callback type of the function to call when the OM has been updated
    /// </summary>
    public delegate void OnUpdatedHandler();

    /// <summary>
    /// Callback to invoke when the object model has been updated
    /// </summary>
    private readonly OnUpdatedHandler _onUpdatedHandler;

    // Private fields
    private readonly ObjectModel _model;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly Settings _settings;

    /// <summary>
    /// CTS to trigger when the lock is being released
    /// </summary>
    private readonly CancellationTokenSource? _releaseCts;

    /// <summary>
    /// Logger instance
    /// </summary>
    private readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Constructor of the lock wrapper
    /// </summary>
    /// <param name="lockItem">Actual lock</param>
    /// <param name="isWriteLock">Whether the lock is a read/write lock</param>
    /// <param name="onUpdated">Callback to invoke when the model has been updated</param>
    /// <param name="model">Object model instance</param>
    /// <param name="lifetime">Lifetime of the application</param>
    /// <param name="settings">Settings of the application</param>
    public LockWrapper(IDisposable lockItem, bool isWriteLock, OnUpdatedHandler onUpdated, IHostApplicationLifetime lifetime, ObjectModel model, IOptions<Settings> settings)
    {
        _lock = lockItem;
        _isWriteLock = isWriteLock;
        _onUpdatedHandler = onUpdated;
        _model = model;
        _lifetime = lifetime;
        _settings = settings.Value;

        if (_settings.MaxMachineModelLockTime > 0)
        {
            _releaseCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);

            StackTrace stackTrace = new(true);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_settings.MaxMachineModelLockTime, _releaseCts.Token);
                    _logger.Fatal("{0} deadlock detected, stack trace of the deadlock:\n{1}", isWriteLock ? "Writer" : "Reader", stackTrace);
                    _lifetime.StopApplication();
                }
                finally
                {
                    _releaseCts.Dispose();
                }
            });
        }
    }

    /// <summary>
    /// Dispose method that is called when the lock is released
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (_isWriteLock)
            {
                // It is safe to assume that the object model has been updated
                _onUpdatedHandler?.Invoke();

                // Clear the messages again if waiting clients could output this message
                if (IPC.Processors.CodeStream.HasClientsWaitingForMessages || IPC.Processors.ModelSubscription.HasClientsWaitingForMessages)
                {
                    _model.Messages.Clear();
                }
            }
        }
        finally
        {
            // Dispose the lock again
            _lock.Dispose();

            // Stop the deadlock detection task if applicable
            if (!_lifetime.ApplicationStopping.IsCancellationRequested)
            {
                _releaseCts?.Cancel();
            }
        }
    }
}
