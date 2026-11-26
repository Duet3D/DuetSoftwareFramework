using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DuetEmulation.Dsf;

/// <summary>
/// DuetControlServer's clock on the DSF bench: emulated time, which the bench moves, with timers that
/// fall due only when it fires them
/// </summary>
/// <remarks>
/// The bench fires one timer at a time and lets DuetControlServer settle before the next, so timers
/// due together run in the order they fall due, then the order they were set, on every run. A
/// callback runs on the thread pool, where the bench watches for it to finish.
/// </remarks>
public sealed class EmulatedTimeProvider : TimeProvider
{
    /// <summary>The wall-clock time emulated time starts from, so that what DuetControlServer stamps repeats</summary>
    public static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly Lock _lock = new();
    private readonly SortedSet<EmulatedTimer> _due = new(DueOrder.Instance);
    private long _nowTicks;
    private long _nextSequence;

    /// <summary>The emulated time</summary>
    public TimeSpan Now
    {
        get
        {
            lock (_lock)
            {
                return TimeSpan.FromTicks(_nowTicks);
            }
        }
    }

    /// <inheritdoc/>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc/>
    public override long GetTimestamp()
    {
        lock (_lock)
        {
            return _nowTicks;
        }
    }

    /// <inheritdoc/>
    public override DateTimeOffset GetUtcNow() => Epoch + Now;

    /// <inheritdoc/>
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    /// <inheritdoc/>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        EmulatedTimer timer = new(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Move the clock on to <paramref name="now"/>; it never goes back</summary>
    public void AdvanceTo(TimeSpan now)
    {
        lock (_lock)
        {
            _nowTicks = Math.Max(_nowTicks, now.Ticks);
        }
    }

    /// <summary>When the next timer falls due, or null if none is set</summary>
    public TimeSpan? NextDue
    {
        get
        {
            lock (_lock)
            {
                return _due.Count == 0 ? null : TimeSpan.FromTicks(_due.Min!.DueTicks);
            }
        }
    }

    /// <summary>Fire the first timer that has fallen due, if any</summary>
    /// <returns>Whether one fired</returns>
    public bool FireNextDue()
    {
        EmulatedTimer? timer;
        lock (_lock)
        {
            timer = _due.Count == 0 ? null : _due.Min;
            if (timer is null || timer.DueTicks > _nowTicks)
            {
                return false;
            }
            _due.Remove(timer);
            if (timer.PeriodTicks > 0)
            {
                timer.DueTicks += timer.PeriodTicks;
                timer.Sequence = _nextSequence++;
                _due.Add(timer);
            }
        }
        ThreadPool.UnsafeQueueUserWorkItem(t => t.Callback(t.State), timer, preferLocal: false);
        return true;
    }

    private bool Change(EmulatedTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        lock (_lock)
        {
            if (timer.Disposed)
            {
                return false;
            }
            _due.Remove(timer);
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                timer.DueTicks = _nowTicks + Math.Max(0, dueTime.Ticks);
                timer.PeriodTicks = period == Timeout.InfiniteTimeSpan ? 0 : Math.Max(0, period.Ticks);
                timer.Sequence = _nextSequence++;
                _due.Add(timer);
            }
            return true;
        }
    }

    private void Dispose(EmulatedTimer timer)
    {
        lock (_lock)
        {
            timer.Disposed = true;
            _due.Remove(timer);
        }
    }

    private sealed class EmulatedTimer(EmulatedTimeProvider provider, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public long DueTicks { get; set; }
        public long PeriodTicks { get; set; }
        public long Sequence { get; set; }
        public bool Disposed { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) => provider.Change(this, dueTime, period);

        public void Dispose() => provider.Dispose(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    // Timers in the order they fire: by when they are due, then by when they were set
    private sealed class DueOrder : IComparer<EmulatedTimer>
    {
        public static readonly DueOrder Instance = new();

        public int Compare(EmulatedTimer? x, EmulatedTimer? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }
            int byDue = x!.DueTicks.CompareTo(y!.DueTicks);
            return byDue != 0 ? byDue : x.Sequence.CompareTo(y.Sequence);
        }
    }
}
