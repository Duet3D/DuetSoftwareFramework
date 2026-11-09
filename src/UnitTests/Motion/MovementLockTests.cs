using System;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer.Motion;
using NUnit.Framework;

namespace UnitTests.Motion;

/// <summary>
/// The movement lock, RepRapFirmware's movement resource (<c>GCodes::LockResource</c> on
/// <c>MoveResourceBase</c>): one channel at a time may command motion, a channel may take it again
/// while it already holds it, a channel can suspend its holds for a wait on another channel's motion,
/// and a prioritised code never takes it
/// </summary>
[TestFixture]
public class MovementLockTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(100);

    [Test]
    public async Task AFreeLockIsTakenAtOnce()
    {
        MovementLock movementLock = new();

        ValueTask<IDisposable> acquire = movementLock.AcquireAsync(CodeChannel.HTTP);
        Assert.That(acquire.IsCompleted, Is.True, "an uncontended lock is taken without waiting");
        IDisposable hold = await acquire;
        Assert.That(movementLock.Owner, Is.EqualTo(CodeChannel.HTTP));

        hold.Dispose();
        Assert.That(movementLock.Owner, Is.Null);
    }

    [Test]
    public async Task AnotherChannelWaitsUntilTheHolderReleases()
    {
        MovementLock movementLock = new();
        IDisposable httpHold = await movementLock.AcquireAsync(CodeChannel.HTTP);

        Task<IDisposable> daemon = movementLock.AcquireAsync(CodeChannel.Daemon).AsTask();
        await Task.Delay(Settle);
        Assert.That(daemon.IsCompleted, Is.False, "the daemon may not command motion while HTTP holds the lock");

        httpHold.Dispose();
        using IDisposable daemonHold = await daemon.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(movementLock.Owner, Is.EqualTo(CodeChannel.Daemon));
    }

    [Test]
    public async Task TheHoldingChannelTakesItAgainAndKeepsItUntilEveryHoldIsReleased()
    {
        MovementLock movementLock = new();
        IDisposable outer = await movementLock.AcquireAsync(CodeChannel.HTTP);

        // A code inside a macro the outer code is running, as G1 inside homex.g under G28
        ValueTask<IDisposable> acquireInner = movementLock.AcquireAsync(CodeChannel.HTTP);
        Assert.That(acquireInner.IsCompleted, Is.True, "the channel that holds the lock is not made to wait for itself");
        IDisposable inner = await acquireInner;

        Task<IDisposable> daemon = movementLock.AcquireAsync(CodeChannel.Daemon).AsTask();
        inner.Dispose();
        await Task.Delay(Settle);
        Assert.That(daemon.IsCompleted, Is.False, "the outer hold still keeps the lock");
        Assert.That(movementLock.Owner, Is.EqualTo(CodeChannel.HTTP));

        outer.Dispose();
        using IDisposable daemonHold = await daemon.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task WaitersAreServedInTheOrderTheyAsked()
    {
        MovementLock movementLock = new();
        IDisposable httpHold = await movementLock.AcquireAsync(CodeChannel.HTTP);

        Task<IDisposable> daemon = movementLock.AcquireAsync(CodeChannel.Daemon).AsTask();
        Task<IDisposable> trigger = movementLock.AcquireAsync(CodeChannel.Trigger).AsTask();

        httpHold.Dispose();
        IDisposable daemonHold = await daemon.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(Settle);
        Assert.That(trigger.IsCompleted, Is.False, "the trigger asked after the daemon, so it waits for the daemon");

        daemonHold.Dispose();
        using IDisposable triggerHold = await trigger.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(movementLock.Owner, Is.EqualTo(CodeChannel.Trigger));
    }

    [Test]
    public async Task EveryWaiterOfTheChannelThatGetsTheLockIsLetIn()
    {
        MovementLock movementLock = new();
        IDisposable httpHold = await movementLock.AcquireAsync(CodeChannel.HTTP);

        Task<IDisposable> daemon1 = movementLock.AcquireAsync(CodeChannel.Daemon).AsTask();
        Task<IDisposable> trigger = movementLock.AcquireAsync(CodeChannel.Trigger).AsTask();
        Task<IDisposable> daemon2 = movementLock.AcquireAsync(CodeChannel.Daemon).AsTask();

        httpHold.Dispose();
        IDisposable[] daemonHolds = await Task.WhenAll(daemon1, daemon2).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(trigger.IsCompleted, Is.False);

        daemonHolds[0].Dispose();
        await Task.Delay(Settle);
        Assert.That(trigger.IsCompleted, Is.False, "the daemon still has a hold");

        daemonHolds[1].Dispose();
        using IDisposable triggerHold = await trigger.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ACancelledWaiterGivesUpItsPlace()
    {
        MovementLock movementLock = new();
        IDisposable httpHold = await movementLock.AcquireAsync(CodeChannel.HTTP);

        using CancellationTokenSource cts = new();
        Task<IDisposable> daemon = movementLock.AcquireAsync(CodeChannel.Daemon, cts.Token).AsTask();
        Task<IDisposable> trigger = movementLock.AcquireAsync(CodeChannel.Trigger).AsTask();

        await cts.CancelAsync();
        Assert.That(async () => await daemon, Throws.InstanceOf<OperationCanceledException>());

        httpHold.Dispose();
        using IDisposable triggerHold = await trigger.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(movementLock.Owner, Is.EqualTo(CodeChannel.Trigger), "the cancelled daemon was skipped");
    }

    [Test]
    public void AnAlreadyCancelledAcquireThrowsWithoutTakingTheLock()
    {
        MovementLock movementLock = new();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.That(async () => await movementLock.AcquireAsync(CodeChannel.HTTP, cts.Token),
                    Throws.InstanceOf<OperationCanceledException>());
        Assert.That(movementLock.Owner, Is.Null);
    }

    [Test]
    public async Task ReleasingAHoldTwiceReleasesItOnce()
    {
        MovementLock movementLock = new();
        IDisposable outer = await movementLock.AcquireAsync(CodeChannel.HTTP);
        IDisposable inner = await movementLock.AcquireAsync(CodeChannel.HTTP);

        inner.Dispose();
        inner.Dispose();
        Assert.That(movementLock.Owner, Is.EqualTo(CodeChannel.HTTP), "the second release of the inner hold must not release the outer one");

        outer.Dispose();
        Assert.That(movementLock.Owner, Is.Null);
    }

    [Test]
    public async Task APrioritisedCodeDoesNotTakeTheLock()
    {
        MovementLock movementLock = new();
        using IDisposable httpHold = await movementLock.AcquireAsync(CodeChannel.HTTP);

        Code code = new("M112") { Channel = CodeChannel.Daemon, Flags = CodeFlags.IsPrioritized };
        ValueTask<IDisposable> acquire = movementLock.AcquireAsync(code);
        Assert.That(acquire.IsCompleted, Is.True, "a prioritised code jumps every queue, this one included");
        (await acquire).Dispose();
        Assert.That(movementLock.Owner, Is.EqualTo(CodeChannel.HTTP), "and neither takes nor releases the lock");
    }

    [Test]
    public async Task AnOrdinaryCodeTakesTheLockForItsChannel()
    {
        MovementLock movementLock = new();

        Code code = new("G1 X10") { Channel = CodeChannel.Daemon };
        using IDisposable hold = await movementLock.AcquireAsync(code);
        Assert.That(movementLock.Owner, Is.EqualTo(CodeChannel.Daemon));
    }

    [Test]
    public async Task ASuspensionLetsAnotherChannelInAndTheRestoreWaitsForIt()
    {
        MovementLock movementLock = new();
        IDisposable outer = await movementLock.AcquireAsync(CodeChannel.HTTP);
        IDisposable inner = await movementLock.AcquireAsync(CodeChannel.HTTP);
        Task<IDisposable> daemon = movementLock.AcquireAsync(CodeChannel.Daemon).AsTask();

        MovementLock.Suspension suspension = movementLock.Suspend(CodeChannel.HTTP);
        IDisposable daemonHold = await daemon.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(movementLock.Owner, Is.EqualTo(CodeChannel.Daemon), "the outer hold is suspended along with the inner one");

        Task restore = suspension.RestoreAsync().AsTask();
        await Task.Delay(Settle);
        Assert.That(restore.IsCompleted, Is.False, "the restore waits while the daemon holds the lock");

        daemonHold.Dispose();
        await restore.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(movementLock.Owner, Is.EqualTo(CodeChannel.HTTP));

        inner.Dispose();
        Assert.That(movementLock.Owner, Is.EqualTo(CodeChannel.HTTP), "the restored outer hold still keeps the lock");
        outer.Dispose();
        Assert.That(movementLock.Owner, Is.Null);
    }

    [Test]
    public async Task ARestoreWaitsBehindAChannelThatAskedFirst()
    {
        MovementLock movementLock = new();
        using IDisposable outer = await movementLock.AcquireAsync(CodeChannel.HTTP);
        MovementLock.Suspension suspension = movementLock.Suspend(CodeChannel.HTTP);

        IDisposable daemonHold = await movementLock.AcquireAsync(CodeChannel.Daemon);
        Task<IDisposable> trigger = movementLock.AcquireAsync(CodeChannel.Trigger).AsTask();
        Task restore = suspension.RestoreAsync().AsTask();

        daemonHold.Dispose();
        IDisposable triggerHold = await trigger.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(Settle);
        Assert.That(restore.IsCompleted, Is.False, "the trigger asked before the restore");

        triggerHold.Dispose();
        await restore.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(movementLock.Owner, Is.EqualTo(CodeChannel.HTTP));
    }

    [Test]
    public async Task SuspendingAChannelThatHoldsNothingChangesNothing()
    {
        MovementLock movementLock = new();
        using IDisposable daemonHold = await movementLock.AcquireAsync(CodeChannel.Daemon);

        MovementLock.Suspension suspension = movementLock.Suspend(CodeChannel.HTTP);
        Assert.That(movementLock.Owner, Is.EqualTo(CodeChannel.Daemon));

        ValueTask restore = suspension.RestoreAsync();
        Assert.That(restore.IsCompleted, Is.True, "there is nothing to take back");
        await restore;
        Assert.That(movementLock.Owner, Is.EqualTo(CodeChannel.Daemon));
    }

    [Test]
    public async Task AHoldReleasedWhileSuspendedLeavesTheOtherChannelsLockAlone()
    {
        MovementLock movementLock = new();
        IDisposable httpHold = await movementLock.AcquireAsync(CodeChannel.HTTP);
        MovementLock.Suspension suspension = movementLock.Suspend(CodeChannel.HTTP);
        using IDisposable daemonHold = await movementLock.AcquireAsync(CodeChannel.Daemon);

        // As an outer code unwinding after a cancellation, before the suspension is restored
        httpHold.Dispose();
        Assert.That(movementLock.Owner, Is.EqualTo(CodeChannel.Daemon));

        ValueTask restore = suspension.RestoreAsync();
        Assert.That(restore.IsCompleted, Is.True, "no suspended hold is left to take back");
        await restore;
        Assert.That(movementLock.Owner, Is.EqualTo(CodeChannel.Daemon));
    }

    [Test]
    public async Task ARestoreWhoseHoldsWereReleasedWhileItWaitedPassesTheLockOn()
    {
        MovementLock movementLock = new();
        IDisposable httpHold = await movementLock.AcquireAsync(CodeChannel.HTTP);
        MovementLock.Suspension suspension = movementLock.Suspend(CodeChannel.HTTP);
        IDisposable daemonHold = await movementLock.AcquireAsync(CodeChannel.Daemon);

        Task restore = suspension.RestoreAsync().AsTask();
        Task<IDisposable> trigger = movementLock.AcquireAsync(CodeChannel.Trigger).AsTask();
        httpHold.Dispose();

        daemonHold.Dispose();
        await restore.WaitAsync(TimeSpan.FromSeconds(5));
        using IDisposable triggerHold = await trigger.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(movementLock.Owner, Is.EqualTo(CodeChannel.Trigger), "the restore brought no holds back, so the trigger got the lock");
    }

    [Test]
    public async Task ACancelledRestoreLeavesTheHoldsSuspended()
    {
        MovementLock movementLock = new();
        IDisposable httpHold = await movementLock.AcquireAsync(CodeChannel.HTTP);
        MovementLock.Suspension suspension = movementLock.Suspend(CodeChannel.HTTP);
        IDisposable daemonHold = await movementLock.AcquireAsync(CodeChannel.Daemon);

        using CancellationTokenSource cts = new();
        Task restore = suspension.RestoreAsync(cts.Token).AsTask();
        await cts.CancelAsync();
        Assert.That(async () => await restore, Throws.InstanceOf<OperationCanceledException>());

        httpHold.Dispose();
        Assert.That(movementLock.Owner, Is.EqualTo(CodeChannel.Daemon), "the suspended hold's release does not touch the daemon's lock");
        daemonHold.Dispose();
        Assert.That(movementLock.Owner, Is.Null, "and the cancelled restore does not take the lock afterwards");
    }

    [Test]
    public async Task APrioritisedCodeDoesNotSuspendItsChannel()
    {
        MovementLock movementLock = new();
        using IDisposable httpHold = await movementLock.AcquireAsync(CodeChannel.HTTP);

        Code code = new("M292") { Channel = CodeChannel.HTTP, Flags = CodeFlags.IsPrioritized };
        MovementLock.Suspension suspension = movementLock.Suspend(code);
        Assert.That(movementLock.Owner, Is.EqualTo(CodeChannel.HTTP));
        await suspension.RestoreAsync();
        Assert.That(movementLock.Owner, Is.EqualTo(CodeChannel.HTTP));
    }
}
