using System;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetControlServer.Codes;
using DuetControlServer.Motion;
using NUnit.Framework;

namespace UnitTests.Codes;

/// <summary>The hold a pause puts on the macros of the job</summary>
[TestFixture]
public class JobMacroHoldTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly MovementLock _movementLock = new();
    private JobMacroHold _hold = null!;

    [SetUp]
    public void SetUp() => _hold = new JobMacroHold(CodeChannel.File, _movementLock);

    [Test]
    public void NothingIsHeldUnlessRequested() => Assert.That(_hold.WaitAsync(CancellationToken.None).IsCompleted, Is.True);

    [Test]
    public async Task AMacroIsHeldUntilTheRelease()
    {
        _hold.Request(evenIfLocked: false);
        Task reached = _hold.WaitUntilReachedAsync();
        Assert.That(reached.IsCompleted, Is.False, "no macro has come to rest yet");

        Task wait = _hold.WaitAsync(CancellationToken.None).AsTask();
        await reached.WaitAsync(Timeout);
        await Task.Delay(Settle);
        Assert.That(wait.IsCompleted, Is.False, "the macro is held");

        _hold.Release();
        await wait.WaitAsync(Timeout);
        Assert.That(_hold.WaitAsync(CancellationToken.None).IsCompleted, Is.True, "the hold is over");
    }

    [Test]
    public async Task AMacroUnderTheMovementLockIsLetFinish()
    {
        using IDisposable lockHold = await _movementLock.AcquireAsync(CodeChannel.File);
        _hold.Request(evenIfLocked: false);
        Task reached = _hold.WaitUntilReachedAsync();
        Assert.That(_hold.WaitAsync(CancellationToken.None).IsCompleted, Is.True, "the code that called the macro holds the lock");
        Assert.That(reached.IsCompleted, Is.False);

        lockHold.Dispose();
        Assert.That(_hold.WaitAsync(CancellationToken.None).IsCompleted, Is.False, "the next code outside it is held");
        await reached.WaitAsync(Timeout);
        _hold.Release();
    }

    [Test]
    public async Task AMacroIsHeldWhileAnotherChannelHasTheMovementLock()
    {
        using IDisposable lockHold = await _movementLock.AcquireAsync(CodeChannel.HTTP);
        _hold.Request(evenIfLocked: false);
        Assert.That(_hold.WaitAsync(CancellationToken.None).IsCompleted, Is.False);
        _hold.Release();
    }

    [Test]
    public async Task ALaterRequestHoldsMacrosUnderTheMovementLockToo()
    {
        using IDisposable lockHold = await _movementLock.AcquireAsync(CodeChannel.File);
        _hold.Request(evenIfLocked: false);
        Task first = _hold.WaitUntilReachedAsync();
        _hold.Request(evenIfLocked: true);
        Assert.That(_hold.WaitUntilReachedAsync(), Is.SameAs(first), "there is one hold");
        _hold.Request(evenIfLocked: false);

        Assert.That(_hold.WaitAsync(CancellationToken.None).IsCompleted, Is.False);
        await first.WaitAsync(Timeout);
        _hold.Release();
    }

    [Test]
    public async Task ACancelledMacroIsNotHeld()
    {
        using CancellationTokenSource source = new();
        _hold.Request(evenIfLocked: true);
        Task wait = _hold.WaitAsync(source.Token).AsTask();

        source.Cancel();
        await wait.WaitAsync(Timeout);
        Assert.That(wait.IsCompletedSuccessfully, Is.True);
        _hold.Release();
    }

    [Test]
    public void AHoldReleasedBeforeItIsReachedIsCancelled()
    {
        _hold.Request(evenIfLocked: false);
        Task reached = _hold.WaitUntilReachedAsync();
        _hold.Release();
        Assert.That(reached.IsCanceled, Is.True);
        Assert.That(() => _hold.Release(), Throws.Nothing, "releasing again does nothing");
    }

    [Test]
    public async Task AMacroIsHeldAgainByTheNextRequest()
    {
        _hold.Request(evenIfLocked: false);
        _hold.Release();

        _hold.Request(evenIfLocked: false);
        Task reached = _hold.WaitUntilReachedAsync();
        Task wait = _hold.WaitAsync(CancellationToken.None).AsTask();
        await reached.WaitAsync(Timeout);
        Assert.That(wait.IsCompleted, Is.False);

        _hold.Release();
        await wait.WaitAsync(Timeout);
    }

    [Test]
    public void AHoldIsRequestedUntilItIsReleased()
    {
        Assert.That(_hold.IsRequested, Is.False);
        Assert.That(_hold.WaitUntilReachedAsync().IsCompletedSuccessfully, Is.True, "there is nothing to wait for");

        _hold.Request(evenIfLocked: false);
        Assert.That(_hold.IsRequested, Is.True);

        _hold.Release();
        Assert.That(_hold.IsRequested, Is.False);
    }

    [Test]
    public async Task TheMovementLockIsGivenUpUntilItIsRestored()
    {
        using IDisposable lockHold = await _movementLock.AcquireAsync(CodeChannel.File);
        _hold.Request(evenIfLocked: true);
        _hold.SuspendMovement();
        Assert.That(_movementLock.Owner, Is.Null);

        IDisposable other = await _movementLock.AcquireAsync(CodeChannel.HTTP);
        Task restore = _hold.RestoreMovementAsync(CancellationToken.None).AsTask();
        Assert.That(restore.IsCompleted, Is.False, "the lock is another channel's");

        other.Dispose();
        await restore.WaitAsync(Timeout);
        Assert.That(_movementLock.Owner, Is.EqualTo(CodeChannel.File));
        _hold.Release();
    }

    [Test]
    public async Task TheMovementLockCanBeGivenUpAgain()
    {
        using IDisposable lockHold = await _movementLock.AcquireAsync(CodeChannel.File);
        _hold.Request(evenIfLocked: true);
        _hold.SuspendMovement();
        await _hold.RestoreMovementAsync(CancellationToken.None);
        _hold.SuspendMovement();
        Assert.That(_movementLock.Owner, Is.Null);

        await _hold.RestoreMovementAsync(CancellationToken.None);
        Assert.That(_movementLock.Owner, Is.EqualTo(CodeChannel.File));
        _hold.Release();
    }

    [Test]
    public async Task TheMovementLockIsRestoredAfterARestoreThatFailed()
    {
        using IDisposable lockHold = await _movementLock.AcquireAsync(CodeChannel.File);
        _hold.Request(evenIfLocked: true);
        _hold.SuspendMovement();

        using CancellationTokenSource source = new();
        IDisposable other = await _movementLock.AcquireAsync(CodeChannel.HTTP);
        Task restore = _hold.RestoreMovementAsync(source.Token).AsTask();
        source.Cancel();
        Assert.That(async () => await restore, Throws.InstanceOf<OperationCanceledException>());
        _hold.SuspendMovement();

        other.Dispose();
        await _hold.RestoreMovementAsync(CancellationToken.None);
        Assert.That(_movementLock.Owner, Is.EqualTo(CodeChannel.File));
        _hold.Release();
    }

    [Test]
    public async Task TheMovementLockIsLeftAloneWithoutAHold()
    {
        using IDisposable lockHold = await _movementLock.AcquireAsync(CodeChannel.File);
        _hold.SuspendMovement();
        await _hold.RestoreMovementAsync(CancellationToken.None);
        Assert.That(_movementLock.Owner, Is.EqualTo(CodeChannel.File));
    }
}
