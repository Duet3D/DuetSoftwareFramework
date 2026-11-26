using System;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetControlServer.Motion;

namespace DuetControlServer.Codes;

/// <summary>
/// Holds the macros the job has called on a channel where they are while the job is paused
/// </summary>
/// <remarks>
/// A job file that is paused drops what it has read ahead and is rewound. A macro cannot be rewound without running
/// its codes again, so it keeps them, and they are dispatched once the hold is released. The macros a pause or a
/// resume runs on the same channel are not the job's and are never held
/// </remarks>
/// <param name="channel">Channel the macros run on</param>
/// <param name="movementLock">Which channel may command motion</param>
public sealed class JobMacroHold(CodeChannel channel, MovementLock movementLock)
{
    /// <summary>
    /// A hold that has been requested and not released yet
    /// </summary>
    private sealed class HoldRequest
    {
        public volatile bool EvenIfLocked;
        public readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MovementLock.Suspension? Movement;
    }

    private readonly Lock _lock = new();
    private volatile HoldRequest? _request;

    /// <summary>
    /// Whether a hold has been requested and not released yet
    /// </summary>
    public bool IsRequested => _request is not null;

    /// <summary>
    /// Hold the macros before the next code they would dispatch
    /// </summary>
    /// <param name="evenIfLocked">
    /// Whether to hold macros that run under a code holding the movement lock, such as those of a tool change or of
    /// homing. They are let finish otherwise
    /// </param>
    public void Request(bool evenIfLocked)
    {
        lock (_lock)
        {
            HoldRequest request = _request ??= new HoldRequest();
            request.EvenIfLocked |= evenIfLocked;
        }
    }

    /// <summary>
    /// Wait until a macro has come to rest
    /// </summary>
    /// <returns>Task that is cancelled when the hold is released first, and has completed if none is requested</returns>
    public Task WaitUntilReachedAsync() => _request?.Reached.Task ?? Task.CompletedTask;

    /// <summary>
    /// Give up the movement lock of the codes that called the macros, so that other channels can move while they are held
    /// </summary>
    public void SuspendMovement()
    {
        // What was given up before and not taken back since is kept
        if (_request is HoldRequest request && movementLock.Owner == channel)
        {
            request.Movement = movementLock.Suspend(channel);
        }
    }

    /// <summary>
    /// Take back the movement lock given up, waiting in turn for it
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public ValueTask RestoreMovementAsync(CancellationToken cancellationToken)
        => _request?.Movement?.RestoreAsync(cancellationToken) ?? ValueTask.CompletedTask;

    /// <summary>
    /// Let the macros carry on
    /// </summary>
    public void Release()
    {
        HoldRequest? request;
        lock (_lock)
        {
            request = _request;
            _request = null;
        }
        request?.Reached.TrySetCanceled();
        request?.Released.TrySetResult();
    }

    /// <summary>
    /// Wait while the macros are held
    /// </summary>
    /// <param name="cancellationToken">Token of the macro</param>
    /// <returns>Asynchronous task, which ends without an error when the token is cancelled</returns>
    /// <remarks>
    /// Called by a macro whose earlier codes have been dispatched, so a movement lock held by the channel belongs to
    /// the code that called the macro
    /// </remarks>
    public async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        while (_request is HoldRequest request && (request.EvenIfLocked || movementLock.Owner != channel))
        {
            request.Reached.TrySetResult();
            try
            {
                await request.Released.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
