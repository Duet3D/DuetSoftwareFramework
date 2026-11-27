using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetControlServer.Motion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

namespace SystemTests.Scenarios.Behaviour.Moves;

/// <summary>
/// MotionService stops when DuetControlServer shuts down, even with moves still to execute
/// </summary>
[TestFixture]
public class MotionServiceShutdownTests : BenchFixture
{
    /// <summary>A move long enough to still be executing when the service is stopped: 100 mm at 10 mm/s</summary>
    private const float Distance = 100.0f, Feed = 600.0f;

    /// <summary>How long the service is given to stop, and how long its CPU use is measured over</summary>
    private static readonly TimeSpan StopWithin = TimeSpan.FromSeconds(2);

    [Test]
    public async Task StoppingDuringAMoveEndsPromptlyWithoutSpinning()
    {
        await using Bench bench = await Bench.StartAsync(Machines.X);
        MovePlanner planner = bench.Host.Services.GetRequiredService<MovePlanner>();
        MotionService motion = bench.Host.Services.GetServices<IHostedService>().OfType<MotionService>().Single();

        await bench.Host.ExecuteCodeAsync(FormattableString.Invariant($"G1 X{Distance} F{Feed}"));
        Stopwatch waited = Stopwatch.StartNew();
        while (!planner.IsMoving && waited.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(10);
        }
        Assert.That(planner.IsMoving, Is.True, "the move is executing before the service is stopped");

        // CPU used while moving, for comparison
        TimeSpan cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        await Task.Delay(StopWithin);
        TimeSpan movingCpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;

        // On a thread of its own: a stop that cancels into a synchronous loop never returns to its caller
        cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        Task stop = Task.Run(() => motion.StopAsync(CancellationToken.None));
        Task executing = motion.ExecuteTask!;
        bool stopped = await Task.WhenAny(executing, Task.Delay(StopWithin)) == executing;
        TimeSpan stoppingCpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
        bool stillMoving = planner.IsMoving;

        // Let the move finish before the bench is disposed, so that a spinning service lets go
        await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(Distance / (Feed / 60.0f) + 10)));

        Assert.Multiple(() =>
        {
            Assert.That(stillMoving, Is.True, "the move was still executing throughout the stop");
            Assert.That(stopped, Is.True, $"MotionService.ExecuteAsync ends within {StopWithin.TotalSeconds} s of being stopped");
            Assert.That(stoppingCpu - movingCpu, Is.LessThan(StopWithin * 0.5),
                        $"stopping uses no more CPU than moving did ({stoppingCpu.TotalMilliseconds:F0} ms against {movingCpu.TotalMilliseconds:F0} ms over {StopWithin.TotalMilliseconds:F0} ms)");
        });
    }
}
