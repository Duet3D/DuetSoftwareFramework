using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace SystemTests.Scenarios.Behaviour.JobControl;

/// <summary>
/// The job control edge cases: how long a job lasts, a pause inside an open while block, G60
/// restore points beside the pause point, a pause during the final move of a job, and pausing with
/// unhomed axes
/// </summary>
[TestFixture]
[GCode("M24", "M25")]
public class JobEdgeCaseTests : BenchFixture
{
    /// <summary>
    /// A job lasts as long as its motion, not as long as its reading. A movement code finishes once
    /// its move is queued, so a job file of a few long moves is read to the end in milliseconds -
    /// and if that ended the job, the machine would report itself idle with the head still moving,
    /// <c>stop.g</c> would run against a machine that had not finished, and every pause commanded
    /// from that point on would be refused for want of a job to pause. The same holds for a job that
    /// ends itself
    /// </summary>
    /// <param name="ending">What the file ends with</param>
    /// <remarks>RRF waits for standstill before StopPrint at the end of the file (GCodes.cpp DoFilePrint) and in GCodes2.cpp cases 0, 1 and 2</remarks>
    [TestCase("")]
    [TestCase("M0")]
    [TestCase("M2")]
    [Category("LongRunning")]
    public async Task JobLastsAsLongAsItsMotion(string ending)
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd =>
            sd.WriteGCode("job.gcode", $"""
                G90
                G1 X80 F3000
                G1 X80 Y30 F6000
                G60 S3
                {ending}
                """)).StartAsync();

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");

        // Every code was read in the first few milliseconds, and most of the motion is still to be made
        await bench.RunToPositionAsync(axis: 0, position: 20);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.Status), Is.EqualTo(MachineStatus.Processing),
                        "the job is still running while the machine works through what it queued");
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.Zero, "so stop.g has not run yet");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].MachinePosition), Is.LessThan(79.0),
                        "and the head has not reached the end of the first move");
        });

        // A pause is still possible, which is the point of staying in the job until the motion ends
        Assert.That(await bench.Host.ExecuteCodeAsync("M25"), Does.Not.Contain("no file is being printed"),
                    "the job is there to be paused");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1),
                        "the job ended once, after the machine had stopped");
            Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((80.0, 30.0)),
                        "having made all of its motion");
        });
    }

    /// <summary>
    /// Loops making moves from X10 Y10 out to X20, X40, X60 and X80 in turn. A pause after X25 lands in the move out to
    /// X40, by when the loop has been read further or to its end
    /// </summary>
    private static readonly TestCaseData[] Loops =
    [
        new TestCaseData("""
            while iterations < 4
                G1 X{20 + iterations * 20} Y30 F6000
                G1 X10 Y10 F6000
            """).SetArgDisplayNames("iterations"),
        new TestCaseData("""
            while iterations < 4
                var x = 20 + iterations * 20
                G1 X{var.x} Y30 F6000
                G1 X{var.x} Y20 F6000
                G1 X10 Y10 F6000
            """).SetArgDisplayNames("variable of the body"),
        new TestCaseData("""
            var count = {0, 0}
            while var.count[1] < 4
                G1 X{20 + var.count[1] * 20} Y30 F6000
                G1 X10 Y10 F6000
                set var.count[1] = var.count[1] + 1
            """).SetArgDisplayNames("variable outside the loop"),
        new TestCaseData("""
            while iterations < 2
                var outer = iterations
                while iterations < 2
                    G1 X{20 + var.outer * 40 + iterations * 20} Y30 F6000
                    if iterations == 5
                        G1 X10 Y10 F600
                    else
                        G1 X10 Y10 F6000
            """).SetArgDisplayNames("nested"),
        new TestCaseData("""
            while iterations < 4
                G1 X{20 + iterations * 20} Y30 F6000 G1 X{20 + iterations * 20} Y20 F6000 G1 X10 Y10 F6000
            """).SetArgDisplayNames("several codes per line"),
        new TestCaseData("""
            while iterations < 4
                G1 X10 Y10 F6000 G1 X{20 + iterations * 20} Y30 F6000
            G1 X10 Y10 F6000
            """).SetArgDisplayNames("second code of a line"),
        new TestCaseData("""
            while iterations < 4
                M98 P"0:/macros/out-and-back.g" X{20 + iterations * 20}
            """).SetArgDisplayNames("restartable macro"),
        new TestCaseData("""
            while iterations < 4
                G1 X{20 + iterations * 20} Y30 F6000
                if iterations > 0
                    G1 X10 Y10 F6000
                    continue
                G1 X10 Y10 F6000
            """).SetArgDisplayNames("continue")
    ];

    /// <summary>A job paused inside a while loop makes the iterations that were left when it resumes</summary>
    /// <remarks>
    /// RRF keeps the loop it has read ahead to (GCodeBuffer::RestartFrom only seeks), so an iteration that was read
    /// before the pause counts as made
    /// </remarks>
    [TestCaseSource(nameof(Loops))]
    [Category("LongRunning")]
    public async Task PauseInsideWhileLoop(string loop)
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd =>
        {
            sd.WriteMacro("out-and-back.g", "M98 R1\nG1 X{param.X} Y30 F6000\nG1 X10 Y10 F6000\n");
            sd.WriteGCode("job.gcode", $"G90\nG1 X10 Y10 F6000\n{loop}\nM400\nG60 S3\n");
        }).StartAsync();

        // Only the second iteration and those after it pass X25
        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.RunToPositionAsync(axis: 0, position: 25);
        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        await AssertEveryIterationWasMadeAsync(bench);
    }

    /// <summary>The local variables of an iteration are put back by a pause that no move of the job comes before</summary>
    [Test]
    public async Task PauseInsideWhileLoopBeforeTheFirstMove()
    {
        const string job = """
            while iterations < 3
                G4 P800
                set global.macroRuns = iterations + 1
                var x = iterations
                if var.x == 2
                    set global.lastX = var.x
            """;
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, "global lastX = 0", Machines.Xye.Homed)
                                             .WithSd(sd => sd.WriteGCode("job.gcode", job)).StartAsync();

        // The variable of the first iteration has been deleted by the time the dwell of the second one is interrupted
        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForGlobalAsync("macroRuns", 1);
        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("lastX"), Is.EqualTo(2), "the last iteration ran");
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the job ended normally");
        });
    }

    /// <summary>DuetControlServer stops at once although a macro of the job has more moves than the queue takes</summary>
    [Test]
    public async Task ShutdownWhileAMacroWaitsForTheQueue()
    {
        Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed).WithSd(sd =>
        {
            sd.WriteMacro("moves.g", "G1 X100 F1000\nG1 X20\nG1 X100\nG1 X20\nG1 X100\n");
            sd.WriteGCode("job.gcode", "M98 P\"0:/macros/moves.g\"\nG1 X0\n");
        }).StartAsync();
        Stopwatch shutdown = new();
        await using (bench)
        {
            await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
            await bench.RunToPositionAsync(axis: 0, position: 5);
            shutdown.Start();
        }
        Assert.That(shutdown.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
    }

    /// <summary>A job that pauses itself inside a while loop makes the iterations that were left when it resumes</summary>
    [Test]
    [Category("LongRunning")]
    public async Task M25InsideWhileLoop()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd => sd.WriteGCode("job.gcode", """
                G90
                G1 X10 Y10 F6000
                while iterations < 4
                    var x = 20 + iterations * 20
                    if iterations == 1
                        M25
                    G1 X{var.x} Y30 F6000
                    G1 X10 Y10 F6000
                M400
                G60 S3
                """)).StartAsync();

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        await AssertEveryIterationWasMadeAsync(bench);
    }

    /// <summary>Assert that the loop went out to X20, X40, X60 and X80 in turn, and came back to X10 Y10</summary>
    private static async Task AssertEveryIterationWasMadeAsync(Bench bench)
    {
        // Where X turned back beyond X10, which is all the loops make it do apart from pause.g parking at X0
        List<int> turns = [];
        int x = 0, lastSteps = 0;
        foreach (ScheduledMove move in bench.CanMaster.ScheduledMoves())
        {
            int steps = move.DriverFor(Machines.DriverBoard, Machines.XDriver)?.Steps ?? 0;
            if (steps == 0)
            {
                continue;
            }
            int position = (int)(x / Machines.XyStepsPerMm);
            if (steps < 0 && lastSteps > 0 && position > 10 && (turns.Count == 0 || turns[^1] != position))
            {
                turns.Add(position);
            }
            x += steps;
            lastSteps = steps;
        }
        Assert.Multiple(async () =>
        {
            Assert.That(turns, Is.EqualTo(new[] { 20, 40, 60, 80 }), "every iteration was made, in order");
            Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((10.0, 10.0)), "final position");
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the job ended normally");
        });
    }

    /// <summary>
    /// G60 restore points beside the pause point: a bare G60 writes slot 0, G60 S3 slot 3, and the
    /// pause writes slot 1 without touching either
    /// </summary>
    [Test]
    [Category("LongRunning")]
    public async Task RestorePointSlotsAreIsolated()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd =>
            sd.WriteGCode("job.gcode", """
                G90
                G1 X30 Y40 F6000
                M400
                G60
                G1 X60 Y80 F6000
                M400
                G60 S3
                G1 X100 Y120 F6000
                M25
                G1 X10 Y10 F6000
                """)).StartAsync();

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.RestorePointAsync(0), Is.EqualTo((30.0, 40.0)), "bare G60 wrote slot 0");
            Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((60.0, 80.0)), "G60 S3 wrote slot 3");
            Assert.That(await bench.Host.RestorePointAsync(1), Is.EqualTo((100.0, 120.0)),
                        "the pause wrote slot 1");
#pragma warning disable CS0618
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.RestorePoints[1].FeedRate), Is.EqualTo(6000.0).Within(0.01),
                        "with the job's feed rate as F gave it");
#pragma warning restore
        });

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.That(await bench.Host.RestorePointAsync(0), Is.EqualTo((30.0, 40.0)),
                    "slot 0 survived the pause untouched");
    }

    /// <summary>
    /// A pause during the final move of a job: the file position near EOF seeks back cleanly, the
    /// resume completes the remainder, and the job then finishes normally instead of hanging
    /// </summary>
    [Test]
    [Category("LongRunning")]
    public async Task PauseDuringFinalMove()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Segmented, Machines.Xye.Homed)
                                             .WithSd(sd =>
            sd.WriteGCode("job.gcode", """
                G90
                G1 X10 Y10 F6000
                G1 X10 Y40 F6000
                G1 X90 Y40 F3000
                """)).StartAsync();

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.RunToPositionAsync(axis: 0, position: 30);
        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        (double pausedX, double pausedY) = await bench.Host.RestorePointAsync(1);
        Assert.Multiple(() =>
        {
            Assert.That(pausedX, Is.InRange(10.5, 89.5), "the pause landed inside the final move");
            Assert.That(pausedY, Is.EqualTo(40.0), "on its line");
        });

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the job ended normally");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Job.LastFileName), Does.EndWith("job.gcode"),
                        "as the last file printed");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Job.LastFileCancelled), Is.False,
                        "not cancelled");
            Assert.That(bench.CanMaster.ScheduledSteps(Machines.XDriver), Is.EqualTo(90 * Machines.XyStepsPerMm),
                        "the final move completed in full");
            Assert.That(bench.CanMaster.ScheduledSteps(Machines.YDriver), Is.EqualTo(40 * Machines.XyStepsPerMm));
        });
    }

    /// <summary>
    /// Pausing with unhomed axes: pause.g and resume.g are both skipped (they are written to lift
    /// and park, which is meaningless on a machine that does not know where it is), but the pause
    /// itself still happens, synchronously and asynchronously
    /// </summary>
    [Test]
    [Category("LongRunning")]
    public async Task PauseUnhomedSkipsTheMacros()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd =>
            sd.WriteGCode("job.gcode", """
                M18
                G91
                G1 X20 F3000
                M25
                G1 X150 F3000
                G1 X-150 F3000
                G90
                """)).StartAsync();

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.That(await bench.Host.GlobalAsync("pauseRan"), Is.Zero,
                    "the synchronous pause settled without running pause.g");

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Processing);
        Assert.That(await bench.Host.GlobalAsync("resumeRan"), Is.Zero, "and resume.g was skipped too");

        // The asynchronous variant behaves the same way
        await Task.Delay(TimeSpan.FromSeconds(1));
        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.That(await bench.Host.GlobalAsync("pauseRan"), Is.Zero, "the feedhold pause skipped pause.g as well");

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("resumeRan"), Is.Zero, "no resume.g on the second resume either");
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the job finished normally");
        });
    }
}
