using System;
using System.Diagnostics;
using System.Threading;

namespace SystemTests;

/// <summary>
/// The master step clock the fake controller reports in every transfer header.
/// </summary>
/// <remarks>
/// The SBC has no step clock of its own: it fits a model to these readings and schedules every move
/// by absolute start time against it, and move retirement (with it deferred-code wake-up and
/// feedhold outcomes) follows from that model. Owning this clock is therefore what makes the fake's
/// motion timeline scriptable; see docs/devel/SYSTEM_EMULATION.md, stage 1
/// </remarks>
internal interface IControllerClock
{
    /// <summary>The current master step clock reading, in ticks of <see cref="SpiWire.StepClockRate"/></summary>
    uint MasterClock { get; }
}

/// <summary>
/// A clock that advances only when the test says so, making the motion timeline deterministic:
/// a test retires moves up to a chosen point by advancing past their scheduled start times.
/// </summary>
/// <remarks>
/// The SBC-side model extrapolates at the nominal rate between the samples it receives and is
/// clamped never to run backwards, so with only the master clock frozen its reading still creeps
/// forward in real time. Full determinism pairs this clock with the pinned local clock seam
/// (<c>DuetSbc_PinLocalClock</c>), advancing both together
/// </remarks>
internal sealed class SteppedClock : IControllerClock
{
    private long _ticks;

    public uint MasterClock => (uint)Interlocked.Read(ref _ticks);

    /// <summary>Advance the clock by the given number of step clocks</summary>
    public void AdvanceBy(uint ticks) => Interlocked.Add(ref _ticks, ticks);

    /// <summary>Advance the clock by the given time span</summary>
    public void AdvanceBy(TimeSpan span) => AdvanceBy((uint)(span.TotalSeconds * SpiWire.StepClockRate));

    /// <summary>Advance the clock to an absolute reading; it never goes backwards</summary>
    public void AdvanceTo(uint ticks)
    {
        long current = Interlocked.Read(ref _ticks);
        while ((uint)current < ticks)
        {
            long replaced = Interlocked.CompareExchange(ref _ticks, ticks, current);
            if (replaced == current)
            {
                return;
            }
            current = replaced;
        }
    }
}

/// <summary>
/// A clock that tracks host time at the nominal step clock rate, for soak-style runs where wall
/// time is acceptable.
/// </summary>
internal sealed class FreeRunningClock : IControllerClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public uint MasterClock => (uint)(_stopwatch.Elapsed.TotalSeconds * SpiWire.StepClockRate);
}
