using DuetSharedLibrary;
using NUnit.Framework;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UnitTests.Utility;

public class AsyncLockTests
{
    [Test]
    public async Task MutualExclusion()
    {
        DuetSharedLibrary.AsyncLock asyncLock = new();
        int concurrent = 0, maxConcurrent = 0, completed = 0;

        async Task Contend()
        {
            for (int i = 0; i < 200; i++)
            {
                using (await asyncLock.LockAsync())
                {
                    maxConcurrent = Math.Max(maxConcurrent, Interlocked.Increment(ref concurrent));
                    await Task.Yield();
                    Interlocked.Decrement(ref concurrent);
                    completed++;      // unsynchronized on purpose, the lock has to make this safe
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(Contend)));
        Assert.That(maxConcurrent, Is.EqualTo(1));
        Assert.That(completed, Is.EqualTo(8 * 200));
    }

    [Test]
    public async Task WaitersAreServedInOrder()
    {
        DuetSharedLibrary.AsyncLock asyncLock = new();
        ConcurrentQueue<int> order = new();
        List<Task> waiters = [];

        using (await asyncLock.LockAsync())
        {
            for (int i = 0; i < 8; i++)
            {
                int index = i;
                if (index % 2 == 0)
                {
                    // Synchronous and asynchronous waiters must share one queue, else they could overtake each other
                    waiters.Add(Task.Factory.StartNew(() =>
                    {
                        using (asyncLock.Lock()) { order.Enqueue(index); }
                    }, TaskCreationOptions.LongRunning));
                }
                else
                {
                    waiters.Add(Task.Run(async () =>
                    {
                        using (await asyncLock.LockAsync()) { order.Enqueue(index); }
                    }));
                }
                await Task.Delay(40);       // let this waiter queue up before the next one starts
            }
        }

        await Task.WhenAll(waiters);
        Assert.That(order, Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }));
    }

    [Test]
    public async Task CancelledWaiterDoesNotConsumeTheLock()
    {
        DuetSharedLibrary.AsyncLock asyncLock = new();
        using CancellationTokenSource cts = new();

        Task cancelled;
        using (await asyncLock.LockAsync())
        {
            cancelled = Task.Run(async () =>
            {
                using (await asyncLock.LockAsync(cts.Token)) { }
            });
            await Task.Delay(40);
            await cts.CancelAsync();
            Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelled);
        }

        // The lock must still be obtainable after a waiter gave up
        using (await asyncLock.LockAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token)) { }
    }
}
