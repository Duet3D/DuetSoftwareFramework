using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SystemTests;

/// <summary>
/// The test seam that pins the local time base the step clock model is fitted against
/// </summary>
/// <remarks>
/// Process-wide, like the <c>CLOCK_MONOTONIC</c> it stands in for, so only one timeline may be
/// pinned at a time. The test assembly declares it rather than DuetControlServer because nothing
/// in the product may call it: against a real controller the model would fit real ticks against a
/// clock that does not move
/// </remarks>
internal static class NativeTestClock
{
    [DllImport("duet_sbc", EntryPoint = "DuetSbc_PinLocalClock")]
    internal static extern void Pin(long ns);

    [DllImport("duet_sbc", EntryPoint = "DuetSbc_UnpinLocalClock")]
    internal static extern void Unpin();
}

/// <summary>
/// The whole motion timeline under the test's control: the master step clock the fake controller
/// reports and the local time base the SBC fits its model against, moved together so that motion
/// happens only while the test is asking for it
/// </summary>
/// <remarks>
/// <para>
/// A pause scenario is about where the machine is when the stop lands, and with a free-running
/// clock that is whatever the host was doing when a <c>Task.Delay</c> expired - so the same
/// scenario stops in a different place on every run, and a fix cannot be told from a scheduling
/// accident. Here the test runs the machine to a position it names and stops it there.
/// </para>
/// <para>
/// Advancing is not free-wheeling: the engine still prepares moves a bounded time ahead and
/// DuetControlServer still has to keep the ring fed, so the timeline is advanced in small steps
/// with the threads given a chance to act on each.
/// </para>
/// <para>
/// That chance is a millisecond of real time per step, which is not yet enough to make the
/// machine's position a function of the timeline alone: how much the motion thread, the transfer
/// loop and DuetControlServer get done in it is a property of the host's scheduler, so the same
/// scenario still stops in different places between runs. Gating the advance on quiescence rather
/// than on a sleep is what closes that, and is also what makes the bench fast enough to run
/// constantly; see docs/devel/DETERMINISTIC_BENCH.md
/// </para>
/// </remarks>
internal sealed class SteppedTimeline : IDisposable
{
    /// <summary>
    /// How much timeline one pass of the advance loop covers. Small enough that the engine sees a
    /// plausible run of samples to fit its model to rather than one jump, which it would reject
    /// </summary>
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(2);

    /// <summary>Real time given to the other threads to act on each step</summary>
    private static readonly TimeSpan StepDwell = TimeSpan.FromMilliseconds(1);

    private readonly SteppedClock _master = new();
    private long _localNs;
    private volatile bool _disposed;

    /// <summary>The clock to give the fake controller, which reports it in every transfer</summary>
    public IControllerClock Clock => _master;

    public SteppedTimeline()
    {
        // From zero, and before anything native exists, so the model's first sample is on this
        // timeline rather than on the monotonic clock it is replacing
        NativeTestClock.Pin(0);
    }

    /// <summary>
    /// Move the timeline on by the given span, giving the motion thread and the transfer loop a
    /// chance to act on each step
    /// </summary>
    public void Advance(TimeSpan span)
    {
        for (TimeSpan moved = TimeSpan.Zero; moved < span; moved += Step)
        {
            _localNs += (long)(Step.TotalSeconds * 1_000_000_000);
            NativeTestClock.Pin(_localNs);
            _master.AdvanceBy(Step);
            Thread.Sleep(StepDwell);
        }
    }

    /// <summary>
    /// Keep the timeline moving until the given condition holds
    /// </summary>
    /// <param name="ready">What the test is waiting for the machine to do</param>
    /// <param name="what">What to say if it never happens</param>
    /// <param name="limit">How much timeline to spend before giving up</param>
    public async Task RunUntilAsync(Func<Task<bool>> ready, string what, TimeSpan? limit = null)
    {
        TimeSpan budget = limit ?? TimeSpan.FromSeconds(30);
        for (TimeSpan spent = TimeSpan.Zero; spent < budget; spent += Step)
        {
            if (await ready())
            {
                return;
            }
            Advance(Step);
        }
        throw new TimeoutException($"{what} did not happen within {budget.TotalSeconds:F1} s of timeline");
    }

    /// <summary>
    /// Run the given work with the timeline moving under it
    /// </summary>
    /// <remarks>
    /// A pause is not an instant: the machine has to run out the moves the stop left standing
    /// before it is at a standstill, and the sequence waits for exactly that. With the timeline
    /// frozen it would wait for ever, so anything that waits on the machine is run through here
    /// </remarks>
    public async Task<T> WhileRunningAsync<T>(Func<Task<T>> work)
    {
        Task<T> task = work();
        while (!task.IsCompleted && !_disposed)
        {
            Advance(Step);
        }
        return await task;
    }

    /// <inheritdoc cref="WhileRunningAsync{T}"/>
    public async Task WhileRunningAsync(Func<Task> work)
        => await WhileRunningAsync<object?>(async () => { await work(); return null; });

    public void Dispose()
    {
        _disposed = true;
        NativeTestClock.Unpin();
    }
}
