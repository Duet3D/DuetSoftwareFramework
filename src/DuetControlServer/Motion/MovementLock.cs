using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;

namespace DuetControlServer.Motion;

/// <summary>
/// Which channel may command motion: RepRapFirmware's movement resource
/// (<c>GCodes::LockResource</c> on <c>MoveResourceBase</c>)
/// </summary>
/// <remarks>
/// <para>
/// It is owned by a channel rather than a code, so the macros a code runs take it again rather than
/// waiting for their caller, and it stays with the channel until every hold is released. Waiting
/// channels are served in the order they asked.
/// </para>
/// <para>
/// A code waiting on something unrelated to motion, such as heaters or a dwell, releases only its own
/// hold. A code waiting for motion that another channel has to make, such as starting or resuming
/// the job (and TODO: a blocking M291 and manual probing, once they are implemented), suspends every
/// hold its channel has and restores them afterwards.
/// </para>
/// </remarks>
public sealed class MovementLock
{
    private readonly Lock _lock = new();
    private CodeChannel? _owner;
    private readonly HashSet<Hold> _holds = [];
    private readonly LinkedList<Waiter> _waiters = [];

    /// <summary>
    /// Channel holding the lock, or null if it is free
    /// </summary>
    public CodeChannel? Owner
    {
        get
        {
            lock (_lock)
            {
                return _owner;
            }
        }
    }

    /// <summary>
    /// Take the lock for a code's channel, unless the code is prioritised
    /// </summary>
    /// <param name="code">Code about to command motion or wait for standstill</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The hold, to be disposed when the code no longer needs the lock</returns>
    /// <exception cref="OperationCanceledException">Cancelled while waiting</exception>
    /// <remarks>A prioritised code jumps every queue, this one included</remarks>
    public ValueTask<IDisposable> AcquireAsync(Code code, CancellationToken cancellationToken = default)
        => code.Flags.HasFlag(CodeFlags.IsPrioritized)
            ? ValueTask.FromResult<IDisposable>(NoHold.Instance)
            : AcquireAsync(code.Channel, cancellationToken);

    /// <summary>
    /// Take the lock for a channel, waiting while another channel holds it
    /// </summary>
    /// <param name="channel">Channel to take it for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The hold, to be disposed when the channel no longer needs the lock</returns>
    /// <exception cref="OperationCanceledException">Cancelled while waiting</exception>
    public ValueTask<IDisposable> AcquireAsync(CodeChannel channel, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Hold hold = new(this);
        Waiter waiter;
        lock (_lock)
        {
            if (_owner is null || _owner == channel)
            {
                _owner = channel;
                _holds.Add(hold);
                return ValueTask.FromResult<IDisposable>(hold);
            }

            waiter = new Waiter(channel, hold, null);
            waiter.Node = _waiters.AddLast(waiter);
        }
        return WaitForHoldAsync(waiter, hold, cancellationToken);
    }

    /// <summary>
    /// Give up every hold a code's channel has, unless the code is prioritised
    /// </summary>
    /// <param name="code">Code about to wait for motion another channel makes</param>
    /// <returns>The suspended holds, to be restored when the wait is over</returns>
    public Suspension Suspend(Code code)
        => code.Flags.HasFlag(CodeFlags.IsPrioritized) ? new Suspension(this, code.Channel) : Suspend(code.Channel);

    /// <summary>
    /// Give up every hold a channel has, so that other channels can move until they are restored
    /// </summary>
    /// <param name="channel">Channel whose holds to suspend</param>
    /// <returns>The suspended holds, to be restored when the wait is over</returns>
    public Suspension Suspend(CodeChannel channel)
    {
        Suspension suspension = new(this, channel);
        List<Waiter>? granted;
        lock (_lock)
        {
            if (_owner != channel)
            {
                return suspension;
            }

            foreach (Hold hold in _holds)
            {
                hold.SuspendedBy = suspension;
                suspension.Holds.Add(hold);
            }
            _holds.Clear();
            granted = HandOver();
        }

        Grant(granted);
        return suspension;
    }

    /// <summary>
    /// Take the lock back for the holds a suspension still has, waiting in turn if another channel
    /// holds it
    /// </summary>
    private ValueTask RestoreAsync(Suspension suspension, CancellationToken cancellationToken)
    {
        Waiter waiter;
        lock (_lock)
        {
            if (suspension.Holds.Count == 0)
            {
                return ValueTask.CompletedTask;
            }
            if (_owner is null || _owner == suspension.Channel)
            {
                _owner = suspension.Channel;
                Reinstate(suspension);
                return ValueTask.CompletedTask;
            }

            waiter = new Waiter(suspension.Channel, null, suspension);
            waiter.Node = _waiters.AddLast(waiter);
        }
        return WaitAsync(waiter, cancellationToken);
    }

    /// <summary>
    /// Wait until a queued acquire is let in
    /// </summary>
    private async ValueTask<IDisposable> WaitForHoldAsync(Waiter waiter, Hold hold, CancellationToken cancellationToken)
    {
        await WaitAsync(waiter, cancellationToken);
        return hold;
    }

    /// <summary>
    /// Wait until a queued waiter is let in
    /// </summary>
    private async ValueTask WaitAsync(Waiter waiter, CancellationToken cancellationToken)
    {
        await using (cancellationToken.Register(() => Cancel(waiter)))
        {
            await waiter.Granted.Task;
        }
    }

    /// <summary>
    /// Withdraw a waiter that has not been let in yet
    /// </summary>
    /// <remarks>A cancelled restore leaves its holds suspended, and they are dropped as they are released</remarks>
    private void Cancel(Waiter waiter)
    {
        lock (_lock)
        {
            if (waiter.Node?.List is null)
            {
                // Let in already: the caller gets the hold and releases it as it unwinds
                return;
            }
            _waiters.Remove(waiter.Node);
        }
        waiter.Granted.TrySetCanceled();
    }

    /// <summary>
    /// Release one hold, handing the lock to the next channel waiting when it was the last
    /// </summary>
    private void Release(Hold hold)
    {
        List<Waiter>? granted;
        lock (_lock)
        {
            if (hold.SuspendedBy is Suspension suspension)
            {
                suspension.Holds.Remove(hold);
                hold.SuspendedBy = null;
                return;
            }
            if (!_holds.Remove(hold) || _holds.Count > 0)
            {
                return;
            }
            granted = HandOver();
        }

        Grant(granted);
    }

    /// <summary>
    /// Give the free lock to the channel that asked first, letting in every waiter of that channel
    /// together because they are one owner. Must be called with the lock held
    /// </summary>
    /// <returns>The waiters let in, to be completed once the lock is released</returns>
    private List<Waiter>? HandOver()
    {
        _owner = null;
        List<Waiter>? granted = null;
        while (_holds.Count == 0 && _waiters.First is LinkedListNode<Waiter> first)
        {
            CodeChannel next = first.Value.Channel;
            for (LinkedListNode<Waiter>? node = first; node is not null;)
            {
                LinkedListNode<Waiter>? following = node.Next;
                Waiter waiter = node.Value;
                if (waiter.Channel == next)
                {
                    _waiters.Remove(node);
                    (granted ??= []).Add(waiter);
                    if (waiter.Hold is Hold hold)
                    {
                        _holds.Add(hold);
                    }
                    else if (waiter.Suspension is Suspension suspension)
                    {
                        Reinstate(suspension);
                    }
                }
                node = following;
            }

            // A restore whose holds were all released meanwhile brings none, so try the next channel
            _owner = _holds.Count > 0 ? next : null;
        }
        return granted;
    }

    /// <summary>
    /// Make a suspension's remaining holds active again. Must be called with the lock held
    /// </summary>
    private void Reinstate(Suspension suspension)
    {
        foreach (Hold hold in suspension.Holds)
        {
            hold.SuspendedBy = null;
            _holds.Add(hold);
        }
        suspension.Holds.Clear();
    }

    /// <summary>
    /// Let in the waiters a hand-over chose, outside the lock
    /// </summary>
    private static void Grant(List<Waiter>? granted)
    {
        if (granted is not null)
        {
            foreach (Waiter waiter in granted)
            {
                waiter.Granted.TrySetResult();
            }
        }
    }

    /// <summary>
    /// The holds a channel gave up for a wait, to be restored when it is over
    /// </summary>
    public sealed class Suspension
    {
        private readonly MovementLock _owner;

        internal Suspension(MovementLock owner, CodeChannel channel)
        {
            _owner = owner;
            Channel = channel;
        }

        /// <summary>
        /// Channel the holds belong to
        /// </summary>
        internal CodeChannel Channel { get; }

        /// <summary>
        /// Holds given up and not released since
        /// </summary>
        internal HashSet<Hold> Holds { get; } = [];

        /// <summary>
        /// Take the lock back for the channel, waiting in turn if another channel holds it
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <exception cref="OperationCanceledException">Cancelled while waiting</exception>
        public ValueTask RestoreAsync(CancellationToken cancellationToken = default)
            => _owner.RestoreAsync(this, cancellationToken);
    }

    /// <summary>
    /// A channel waiting for the lock, to take a new hold or to restore suspended ones
    /// </summary>
    private sealed class Waiter(CodeChannel channel, Hold? hold, Suspension? suspension)
    {
        public CodeChannel Channel { get; } = channel;
        public Hold? Hold { get; } = hold;
        public Suspension? Suspension { get; } = suspension;
        public TaskCompletionSource Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node { get; set; }
    }

    /// <summary>
    /// One hold on the lock, released once however often it is disposed
    /// </summary>
    internal sealed class Hold(MovementLock owner) : IDisposable
    {
        private int _released;

        /// <summary>
        /// Suspension the hold is part of, or null while it is active
        /// </summary>
        public Suspension? SuspendedBy { get; set; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(this);
            }
        }
    }

    /// <summary>
    /// What a prioritised code holds: nothing
    /// </summary>
    private sealed class NoHold : IDisposable
    {
        public static readonly NoHold Instance = new();

        public void Dispose() { }
    }
}
