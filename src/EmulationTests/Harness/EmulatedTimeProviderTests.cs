using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using DuetEmulation.Dsf;
using NUnit.Framework;

namespace EmulationTests.Harness;

/// <summary>
/// DuetControlServer's clock on the DSF bench, which moves only when the bench moves it
/// </summary>
[TestFixture]
public class EmulatedTimeProviderTests
{
    [Test]
    public void TheClockReadsEmulatedTime()
    {
        EmulatedTimeProvider clock = new();
        long start = clock.GetTimestamp();
        clock.AdvanceTo(TimeSpan.FromSeconds(1.5));

        Assert.That(clock.GetElapsedTime(start), Is.EqualTo(TimeSpan.FromSeconds(1.5)));
        Assert.That(clock.GetUtcNow(), Is.EqualTo(EmulatedTimeProvider.Epoch + TimeSpan.FromSeconds(1.5)));
        Assert.That(clock.GetLocalNow().Offset, Is.EqualTo(TimeSpan.Zero));
        clock.AdvanceTo(TimeSpan.FromSeconds(1));
        Assert.That(clock.Now, Is.EqualTo(TimeSpan.FromSeconds(1.5)), "the clock never goes back");
    }

    [Test]
    public async Task TimersFireOnlyWhenFiredInTheOrderTheyFallDue()
    {
        EmulatedTimeProvider clock = new();
        ConcurrentQueue<string> fired = new();
        using ITimer late = clock.CreateTimer(_ => fired.Enqueue("late"), null, TimeSpan.FromMilliseconds(20), Timeout.InfiniteTimeSpan);
        using ITimer first = clock.CreateTimer(_ => fired.Enqueue("first"), null, TimeSpan.FromMilliseconds(10), Timeout.InfiniteTimeSpan);
        using ITimer second = clock.CreateTimer(_ => fired.Enqueue("second"), null, TimeSpan.FromMilliseconds(10), Timeout.InfiniteTimeSpan);

        Assert.That(clock.NextDue, Is.EqualTo(TimeSpan.FromMilliseconds(10)));
        Assert.That(clock.FireNextDue(), Is.False, "nothing falls due until the clock moves");

        clock.AdvanceTo(TimeSpan.FromMilliseconds(15));
        Assert.That(clock.FireNextDue(), Is.True);
        await WaitForCount(fired, 1);
        Assert.That(clock.FireNextDue(), Is.True);
        await WaitForCount(fired, 2);
        Assert.That(clock.FireNextDue(), Is.False);
        Assert.That(fired, Is.EqualTo(new[] { "first", "second" }), "timers due together fire in the order they were set");
        Assert.That(clock.NextDue, Is.EqualTo(TimeSpan.FromMilliseconds(20)));
    }

    [Test]
    public async Task APeriodicTimerFallsDueAgain()
    {
        EmulatedTimeProvider clock = new();
        ConcurrentQueue<int> fired = new();
        using ITimer timer = clock.CreateTimer(_ => fired.Enqueue(1), null, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10));
        clock.AdvanceTo(TimeSpan.FromMilliseconds(25));
        while (clock.FireNextDue())
        {
        }
        await WaitForCount(fired, 2);
        Assert.That(clock.NextDue, Is.EqualTo(TimeSpan.FromMilliseconds(30)));
    }

    [Test]
    public async Task ADelayOnTheClockEndsWhenItsTimerFires()
    {
        EmulatedTimeProvider clock = new();
        Task delay = Task.Delay(TimeSpan.FromSeconds(5), clock);
        Assert.That(delay.IsCompleted, Is.False);
        clock.AdvanceTo(TimeSpan.FromSeconds(5));
        clock.FireNextDue();
        await delay.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public void AChangedOrDisposedTimerDoesNotFire()
    {
        EmulatedTimeProvider clock = new();
        ITimer timer = clock.CreateTimer(_ => Assert.Fail("fired"), null, TimeSpan.FromMilliseconds(10), Timeout.InfiniteTimeSpan);
        timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Assert.That(clock.NextDue, Is.Null);
        timer.Change(TimeSpan.FromMilliseconds(10), Timeout.InfiniteTimeSpan);
        timer.Dispose();
        clock.AdvanceTo(TimeSpan.FromSeconds(1));
        Assert.That(clock.FireNextDue(), Is.False);
    }

    private static async Task WaitForCount<T>(ConcurrentQueue<T> queue, int count)
    {
        for (int i = 0; i < 500 && queue.Count < count; i++)
        {
            await Task.Delay(10);
        }
        Assert.That(queue, Has.Count.EqualTo(count));
    }
}
