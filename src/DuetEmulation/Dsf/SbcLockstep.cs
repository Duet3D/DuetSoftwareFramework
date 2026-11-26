using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DuetEmulation.Emulator;

namespace DuetEmulation.Dsf;

/// <summary>
/// Runs the SBC side (DuetControlServer and DuetRealtimeCore, in-process) in lockstep with the
/// emulated machines, so that a DSF run does the same things at the same emulated times on every run
/// </summary>
/// <remarks>
/// <para>
/// Emulated time is the only clock either side reads: DuetControlServer's through
/// <see cref="EmulatedTimeProvider"/>, DuetRealtimeCore's through its pinned local clock. The SBC side
/// works in no emulated time at all: the emulation runs only once the SBC side has settled, that is
/// once every native thread, every dedicated DuetControlServer thread and the thread pool are all
/// waiting (<see cref="Settle"/>).
/// </para>
/// <para>
/// Between runs, the emulator applies the exchanges the SBC has sent (its SPI model's <c>Sync</c>), and
/// the frames the emulator sent back are released to the SBC one at a time, each once the SBC's clock
/// has been moved to the emulated time it was sent at, as are the timers that fall due, in time order.
/// What the SBC does in reply reaches the emulator at the end of the run that delivered the frame, so
/// it sees the controller up to a quantum late.
/// </para>
/// </remarks>
internal sealed class SbcLockstep
{
    private const string LibraryName = "duet_realtime_core";

    // Samples of the SBC side, at least this far apart, that must all find it waiting
    private const int SettledSamples = 3;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMicroseconds(100);

    // Real time after which the SBC side is taken to be stuck rather than busy
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(60);

    // Real time the frames the emulator says it has sent may take to arrive
    private static readonly TimeSpan FrameArrivalTimeout = TimeSpan.FromSeconds(10);

    private static long s_hostPoolThreads;

    // The longest run of the emulation while the SBC link is idle
    private static readonly TimeSpan MaxSlice = TimeSpan.FromMilliseconds(8);

    private readonly EmulatedSystem _system;
    private TimeSpan _slice;
    private bool _linkActive = true;
    private uint _exchangesSynced;
    private bool _connectionSynced;
    private TimeSpan _sbcTime;

    /// <summary>DuetControlServer's clock</summary>
    public EmulatedTimeProvider Clock { get; } = new();

    /// <summary>The main board's peripheral that carries the SBC's SPI bus</summary>
    public string SbcBridge { get; }

    /// <summary>
    /// Pin the SBC's clocks at the start of emulated time. Before anything native exists, so that the
    /// step clock's first sample is on emulated time
    /// </summary>
    public SbcLockstep(EmulatedSystem system, string sbcBridge)
    {
        DcsHost.RegisterNativeLibrary();
        s_hostPoolThreads = MeasureHostPoolThreads();
        _system = system;
        SbcBridge = sbcBridge;
        SetSbcTime(system.Time);
    }

    /// <summary>Stop pinning DuetRealtimeCore's clock</summary>
    public static void Release() => DuetRT_UnpinLocalClock();

    /// <summary>
    /// Let <paramref name="time"/> of emulated time pass, a quantum at a time, with the SBC side
    /// settled before each quantum and given everything that fell due in it
    /// </summary>
    public async Task RunForAsync(TimeSpan time, CancellationToken cancellationToken)
    {
        TimeSpan until = _system.Time + time;
        while (_system.Time < until)
        {
            await CatchUpAsync(cancellationToken);
            TimeSpan slice = NextSlice(until);
            await _system.RunForAsync(slice, cancellationToken);
        }
        await CatchUpAsync(cancellationToken);
    }

    // How long to run before the SBC side next catches up: a quantum while the link is busy, so that
    // the SBC answers the controller as promptly as it would; longer, up to MaxSlice, while it is
    // idle, since each run of the emulation costs far more real time than a quantum of it. Never past
    // the end of the wait or DuetControlServer's next timer, and always whole quanta
    private TimeSpan NextSlice(TimeSpan until)
    {
        TimeSpan quantum = _system.Quantum;
        _slice = _linkActive ? quantum : TimeSpan.FromTicks(Math.Min(_slice.Ticks * 2, MaxSlice.Ticks));
        _linkActive = false;
        TimeSpan limit = until - _system.Time;
        if (Clock.NextDue is TimeSpan due)
        {
            limit = due - _system.Time < limit ? due - _system.Time : limit;
        }
        long quanta = Math.Max(1, Math.Min(_slice.Ticks, (limit.Ticks + quantum.Ticks - 1) / quantum.Ticks * quantum.Ticks) / quantum.Ticks);
        return TimeSpan.FromTicks(quanta * quantum.Ticks);
    }

    /// <summary>
    /// Bring the SBC side up to the present emulated time without letting any pass: apply what it has
    /// sent, give it what the emulator sent and the timers that fell due, in time order, and let it settle
    /// </summary>
    public async Task CatchUpAsync(CancellationToken cancellationToken)
    {
        TimeSpan now = _system.Time;
        while (true)
        {
            Settle();
            await SyncWithEmulatorAsync(cancellationToken);

            TimeSpan? frame = DuetRT_EmulatorNextFrameTime(out long frameNs) != 0 ? TimeSpan.FromTicks(frameNs / 100) : null;
            TimeSpan? timer = Clock.NextDue;
            TimeSpan? next = frame is null ? timer : timer is null ? frame : (frame <= timer ? frame : timer);
            if (next is not TimeSpan due || due > now)
            {
                break;
            }

            SetSbcTime(due);
            if (frame == due)
            {
                DuetRT_EmulatorReleaseFrame();
                _linkActive = true;
            }
            else
            {
                Clock.FireNextDue();
            }
        }
        SetSbcTime(now);
        Settle();
    }

    /// <summary>
    /// Wait, in real time, until the SBC side is waiting for something: the native threads and
    /// DuetControlServer's dedicated threads parked with no wake-up outstanding, and nothing queued on
    /// or running in the thread pool, other than the calling thread and pool threads parked in a native wait
    /// </summary>
    /// <exception cref="TimeoutException">It stayed busy for <see cref="SettleTimeout"/></exception>
    public static void Settle()
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        int settled = 0;
        long completed = -1;
        while (settled < SettledSamples)
        {
            long nowCompleted = ThreadPool.CompletedWorkItemCount;
            settled = IsWaiting() && nowCompleted == completed ? settled + 1 : 0;
            completed = nowCompleted;
            if (elapsed.Elapsed > SettleTimeout)
            {
                throw new TimeoutException($"The SBC side did not settle in {SettleTimeout.TotalSeconds}s: {Describe()}");
            }
            SpinFor(SampleInterval);
        }
    }

    private static bool IsWaiting()
    {
        long busy = BusyPoolThreads();
        if (busy < s_hostPoolThreads)
        {
            // One of the host's threads has finished
            s_hostPoolThreads = busy;
        }
        return DuetRT_GetPendingActivity() == 0 && ThreadPool.PendingWorkItemCount == 0 && busy <= s_hostPoolThreads;
    }

    private static long BusyPoolThreads()
    {
        ThreadPool.GetMaxThreads(out int maxWorkers, out int maxIo);
        ThreadPool.GetAvailableThreads(out int availableWorkers, out int availableIo);
        return maxWorkers - availableWorkers + (maxIo - availableIo) - DuetRT_GetParkedElsewhere()
               - (Thread.CurrentThread.IsThreadPoolThread ? 1 : 0);
    }

    private static string Describe() =>
        $"native activity {DuetRT_GetPendingActivity()}, pool items queued {ThreadPool.PendingWorkItemCount}, " +
        $"busy pool threads {BusyPoolThreads()} besides {s_hostPoolThreads} of the host's";

    // Pool threads the process keeps busy before the SBC side exists (a test host blocks one reading
    // its control channel), which are not DuetControlServer's. The fewest seen over a few samples, so
    // that work that happens to be running is not taken for it.
    private static long MeasureHostPoolThreads()
    {
        long fewest = long.MaxValue;
        for (int i = 0; i < SettledSamples * 10; i++)
        {
            fewest = Math.Min(fewest, BusyPoolThreads());
            SpinFor(SampleInterval);
        }
        return Math.Max(0, fewest);
    }

    private static void SpinFor(TimeSpan time)
    {
        long until = Stopwatch.GetTimestamp() + (long)(time.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < until)
        {
            Thread.Yield();
        }
    }

    // Have the emulator apply what the SBC has sent, and wait for what the emulator has sent back to arrive
    private async Task SyncWithEmulatorAsync(CancellationToken cancellationToken)
    {
        if (DuetRT_EmulatorGetCounts(out uint exchangesSent, out _) == 0)
        {
            return;
        }
        if (_connectionSynced && exchangesSent == _exchangesSynced)
        {
            return;
        }

        ulong framesSent = await _system.Renode.ExecuteForNumberAsync($"{SbcBridge} Sync 1 {exchangesSent}", _system.Main.Machine, cancellationToken);
        _linkActive = true;
        _connectionSynced = true;
        _exchangesSynced = exchangesSent;

        Stopwatch elapsed = Stopwatch.StartNew();
        while (DuetRT_EmulatorGetCounts(out _, out uint framesReceived) != 0 && framesReceived < framesSent)
        {
            if (elapsed.Elapsed > FrameArrivalTimeout)
            {
                throw new RenodeException($"The emulator sent {framesSent} frames to the SBC, of which {framesReceived} arrived");
            }
            SpinFor(SampleInterval);
        }
    }

    // Move both of the SBC's clocks on; they never go back
    private void SetSbcTime(TimeSpan time)
    {
        _sbcTime = time > _sbcTime ? time : _sbcTime;
        Clock.AdvanceTo(_sbcTime);
        DuetRT_PinLocalClock(_sbcTime.Ticks * 100);
    }

    [DllImport(LibraryName)]
    private static extern void DuetRT_PinLocalClock(long ns);

    [DllImport(LibraryName)]
    private static extern void DuetRT_UnpinLocalClock();

    [DllImport(LibraryName)]
    private static extern long DuetRT_GetPendingActivity();

    [DllImport(LibraryName)]
    private static extern long DuetRT_GetParkedElsewhere();

    [DllImport(LibraryName)]
    private static extern int DuetRT_EmulatorGetCounts(out uint framesSent, out uint framesReceived);

    [DllImport(LibraryName)]
    private static extern int DuetRT_EmulatorNextFrameTime(out long ns);

    [DllImport(LibraryName)]
    private static extern int DuetRT_EmulatorReleaseFrame();
}
