using System;
using System.Diagnostics;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Codes;

/// <summary>
/// G4, the dwell, asserted against RepRapFirmware's <c>GCodes::DoDwell</c>
/// (lib/RepRapFirmware/src/GCodes/GCodes.cpp): S is seconds and P is milliseconds, S wins where
/// both are given, a dwell of zero or less is nothing to do, the machine is brought to a
/// standstill first but only for a channel that has commanded motion since it last waited, and a
/// simulated job does not spend the time it is measuring
/// </summary>
[TestFixture]
public class DwellCodeTests : SystemTests.Host.BenchFixture
{
    /// <summary>How long a code took to come back</summary>
    private static async Task<TimeSpan> TimeAsync(Func<Task> work)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        await work();
        return stopwatch.Elapsed;
    }

    /// <summary>
    /// The margin allowed below a nominal dwell. <see cref="Task.Delay(int, System.Threading.CancellationToken)"/>
    /// is not permitted to return early, but the stopwatch is started outside the pipeline the
    /// code still has to travel, so the comparison is against slightly less than the whole
    /// </summary>
    private static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(30);

    /// <summary>
    /// A dwell of zero, a dwell with no parameter at all and a negative dwell are all nothing to
    /// do, and come back without waiting
    /// </summary>
    /// <remarks>
    /// GCodes.cpp DoDwell reads S as seconds, P as milliseconds and zero when neither is given,
    /// then returns ok for any dwell at or below zero
    /// </remarks>
    [TestCase("G4", TestName = "G4 with no parameter")]
    [TestCase("G4 S0", TestName = "G4 S0")]
    [TestCase("G4 P0", TestName = "G4 P0")]
    [TestCase("G4 S-1", TestName = "G4 with a negative S")]
    [TestCase("G4 P-500", TestName = "G4 with a negative P")]
    public async Task G4WithNothingToWaitForReturnsAtOnce(string code)
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        TimeSpan elapsed = await TimeAsync(() => bench.Host.ExecuteCodeAsync(code));
        Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(1)),
                    $"\"{code}\" is a dwell of zero or less and returns ok without waiting (GCodes.cpp DoDwell)");
    }

    /// <summary>P is a dwell in milliseconds</summary>
    /// <remarks>GCodes.cpp DoDwell: "P value are in milliseconds", read as an integer</remarks>
    [Test]
    public async Task G4PDwellsForMilliseconds()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        TimeSpan elapsed = await TimeAsync(() => bench.Host.ExecuteCodeAsync("G4 P400"));
        Assert.That(elapsed, Is.GreaterThan(TimeSpan.FromMilliseconds(400) - Slack),
                    "G4 P400 waits 400 ms (GCodes.cpp DoDwell)");
    }

    /// <summary>S is a dwell in seconds, and may be fractional</summary>
    /// <remarks>
    /// GCodes.cpp DoDwell: "S values are in seconds", read as a float and multiplied by 1000
    /// </remarks>
    [Test]
    public async Task G4SDwellsForSeconds()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        TimeSpan elapsed = await TimeAsync(() => bench.Host.ExecuteCodeAsync("G4 S0.4"));
        Assert.That(elapsed, Is.GreaterThan(TimeSpan.FromMilliseconds(400) - Slack),
                    "G4 S0.4 waits 0.4 s (GCodes.cpp DoDwell)");
    }

    /// <summary>With both given, S is the one that counts</summary>
    /// <remarks>
    /// GCodes.cpp DoDwell tests S first and only falls back to P when S was not seen, so
    /// P is never read when S is present
    /// </remarks>
    [Test]
    public async Task G4SIsPreferredToP()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        TimeSpan elapsed = await TimeAsync(() => bench.Host.ExecuteCodeAsync("G4 S0.4 P10000"));
        Assert.Multiple(() =>
        {
            Assert.That(elapsed, Is.GreaterThan(TimeSpan.FromMilliseconds(400) - Slack),
                        "S0.4 is the dwell that is taken (GCodes.cpp DoDwell)");
            Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(5)),
                        "P10000 is not read at all when S is present (GCodes.cpp DoDwell)");
        });
    }

    /// <summary>
    /// A channel that has commanded motion waits for the machine to stop before the dwell begins,
    /// so the queued move has been made by the time G4 comes back
    /// </summary>
    /// <remarks>
    /// GCodes.cpp DoDwell calls LockCurrentMovementSystemAndWaitForStandstill when
    /// gb.WasMotionCommanded(), which GCodes.cpp FinaliseMove sets for every move the stream
    /// builds. The timeline is frozen while the assertion is made, so the wait cannot end by the
    /// machine happening to be quick
    /// </remarks>
    [Test]
    public async Task G4WaitsForStandstillWhenTheChannelCommandedMotion()
    {
        using SteppedTimeline timeline = new();
        await using JobBench bench = await JobControlBench.StartSteppedAsync(timeline);

        await timeline.WhileRunningAsync(() => bench.Host.ExecuteCodeAsync("G90"));
        await bench.Host.ExecuteCodeAsync("G1 X20 F600");

        Task<string> dwell = bench.Host.ExecuteCodeAsync("G4 P0");
        await Task.Delay(250);
        Assert.That(dwell.IsCompleted, Is.False,
                    "G4 does not begin until the moves this channel commanded have been made (GCodes.cpp DoDwell)");

        await timeline.WhileRunningAsync(() => dwell);

        // All but arrived rather than exactly at the target: machinePosition is a periodic live
        // report from the engine, so it lags the move that has actually retired by up to one report
        Assert.That(await bench.Host.MachinePositionAsync(0), Is.GreaterThan(19.0),
                    "the queued move had been made by the time G4 returned");
    }

    /// <summary>
    /// A channel that has commanded no motion does not wait, which is what lets a trigger or
    /// daemon macro dwell while a print is running
    /// </summary>
    /// <remarks>
    /// GCodes.cpp DoDwell: "Only do this if motion has been commanded from this GCode stream since
    /// we last waited for motion to stop. This is so that G4 can be used in a trigger or daemon
    /// macro file without pausing motion, when the macro doesn't itself command any motion."
    /// </remarks>
    [Test]
    public async Task G4DoesNotWaitOnAChannelThatCommandedNoMotion()
    {
        using SteppedTimeline timeline = new();
        await using JobBench bench = await JobControlBench.StartSteppedAsync(timeline);

        await timeline.WhileRunningAsync(() => bench.Host.ExecuteCodeAsync("G90"));
        await bench.Host.ExecuteCodeAsync("G1 X20 F600");

        await bench.Host.ExecuteCodeAsync("G4 P0", CodeChannel.Daemon, timeoutMs: 5_000);
        // The timeline stays frozen, so the move commanded above is still outstanding. A dwell
        // that waited for the machine would never come back
        Assert.That(await bench.Host.MachinePositionAsync(0), Is.LessThan(20.0),
                    "the move commanded from HTTP is still outstanding");
    }

    /// <summary>
    /// A dwell read from a file being simulated is not spent: the simulation runs to its end
    /// rather than sitting out the dwell
    /// </summary>
    /// <remarks>
    /// GCodes.cpp DoDwell adds the dwell to simulationTime and returns ok instead of waiting,
    /// unless the code comes from the daemon or a trigger. DuetControlServer measures a simulation
    /// by the wall clock rather than accumulating a simulated time, so the dwell is skipped and
    /// nothing is added; see MCODE_MIGRATION.md section 18
    /// </remarks>
    [Test]
    public async Task G4DoesNotDwellWhileSimulatingAFile()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareSd: sd =>
            sd.WriteGCode("sim.gcode", "G90\nG1 X10 Y10 F3000\nG4 S600\nG1 X0 Y0 F3000\n"));

        TimeSpan elapsed = await TimeAsync(async () =>
        {
            await bench.Host.ExecuteCodeAsync("M37 P\"0:/gcodes/sim.gcode\" F0");
            await bench.Host.WaitForStatusAsync(MachineStatus.Idle, timeoutMs: 60_000);
        });

        Assert.That(elapsed, Is.LessThan(TimeSpan.FromMinutes(1)),
                    "the ten-minute dwell in the simulated file is not waited out (GCodes.cpp DoDwell)");
    }
}
