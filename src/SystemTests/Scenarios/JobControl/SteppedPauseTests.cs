using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios;

/// <summary>
/// The pause and resume edge cases, run against a motion timeline the test drives. Each of these
/// stops the machine at a position the scenario names rather than after a delay, which is what lets
/// them assert where the stop landed and what the job made of it. See <see cref="SteppedTimeline"/>
/// for why that matters: the same scenario against the wall clock stops somewhere different on
/// every run, and a fix cannot be told from a scheduling accident
/// </summary>
[TestFixture]
public class SteppedPauseTests : BenchFixture
{
    /// <summary>
    /// Every scenario here pauses inside a line, so every one needs the line cut into segments to
    /// have a boundary inside it to stop at, and a <c>pause.g</c> that does not park so the position
    /// the stop left the machine at is still readable
    /// </summary>
    private static async Task<(SteppedTimeline Timeline, JobBench Bench)> StartAsync(string job,
                                                                                     string configExtra = "")
    {
        SteppedTimeline timeline = new();
        try
        {
            JobBench bench = await JobControlBench.StartSteppedAsync(timeline,
                configExtra: JobControlBench.SegmentedMoves + "\n" + configExtra,
                prepareSd: sd =>
                {
                    sd.WriteSys("pause.g", "set global.pauseRan = global.pauseRan + 1\n");
                    sd.WriteGCode("job.gcode", job);
                });
            return (timeline, bench);
        }
        catch
        {
            timeline.Dispose();
            throw;
        }
    }

    /// <summary>Start the job and run it until the head reaches the given X</summary>
    private static async Task RunToAsync(SteppedTimeline timeline, JobBench bench, double x)
    {
        await timeline.WhileRunningAsync(() => bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\""));
        await bench.RunToPositionAsync(timeline, axis: 0, position: x);
    }

    /// <summary>Pause where the machine now stands, and wait for the pause to settle</summary>
    private static async Task PauseAsync(SteppedTimeline timeline, JobBench bench)
    {
        await timeline.WhileRunningAsync(() => bench.Host.ExecuteCodeAsync("M25"));
        await timeline.WhileRunningAsync(() => bench.Host.WaitForStatusAsync(MachineStatus.Paused));
    }

    /// <summary>Resume and let the job run to its end</summary>
    private static async Task ResumeToEndAsync(SteppedTimeline timeline, JobBench bench)
    {
        await timeline.WhileRunningAsync(() => bench.Host.ExecuteCodeAsync("M24"));
        await timeline.WhileRunningAsync(() => bench.Host.WaitForStatusAsync(MachineStatus.Idle, 120_000));
    }

    /// <summary>
    /// A relative job paused part-way through one of its lines makes exactly the distance the file
    /// asked for. Each line is a distance to travel rather than a place to be, so a resume that
    /// re-reads one without knowing how much of it was already made travels it a second time
    /// </summary>
    [Test]
    public async Task RelativeJobMakesItsDistanceAcrossAPause()
    {
        (SteppedTimeline timeline, JobBench bench) = await StartAsync("""
            G91
            G1 X100 F6000
            G1 X100
            G1 X100
            G1 X100
            G90
            G60 S3
            """);
        using (timeline)
        await using (bench)
        {
            // Into the last of the four, by which point the read-ahead has run out the file and
            // executed the G90 that closes it. That is the state the bug needs: the mode the line
            // was read in is no longer the mode the channel is in
            await RunToAsync(timeline, bench, 350);
            await PauseAsync(timeline, bench);
            await ResumeToEndAsync(timeline, bench);

            Assert.Multiple(async () =>
            {
                Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((400.0, 0.0)),
                            "the four relative moves total 400 mm however the pause fell");
                Assert.That(bench.CanMaster.ScheduledSteps(driver: 0), Is.EqualTo(400 * 80),
                            "and the steps the boards were given say the same");
            });
        }
    }

    /// <summary>
    /// The same, paused twice inside the same line: the fraction recorded by the second stop is a
    /// fraction of the whole line however many times the job has been stopped inside it, so the two
    /// stops compose rather than each asking for the line again
    /// </summary>
    [Test]
    public async Task TwoStopsInsideOneRelativeLineCompose()
    {
        (SteppedTimeline timeline, JobBench bench) = await StartAsync("""
            G91
            G1 X400 F6000
            G90
            G60 S3
            """);
        using (timeline)
        await using (bench)
        {
            await RunToAsync(timeline, bench, 100);
            await PauseAsync(timeline, bench);
            await timeline.WhileRunningAsync(() => bench.Host.ExecuteCodeAsync("M24"));
            await bench.RunToPositionAsync(timeline, axis: 0, position: 250);
            await PauseAsync(timeline, bench);
            await ResumeToEndAsync(timeline, bench);

            Assert.Multiple(async () =>
            {
                Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((400.0, 0.0)),
                            "the line still ends 400 mm along whatever it was stopped inside");
                Assert.That(bench.CanMaster.ScheduledSteps(driver: 0), Is.EqualTo(400 * 80));
            });
        }
    }

    /// <summary>
    /// The restore point holds the position the machine came to rest at. A stop cannot recall a move
    /// whose segments are on their way to the boards, so those carry the head on past where the stop
    /// was planned, and reading the position at that moment records somewhere it never stopped
    /// </summary>
    [Test]
    public async Task TheRestorePointIsWhereTheMachineCameToRest()
    {
        (SteppedTimeline timeline, JobBench bench) = await StartAsync("""
            G90
            G1 X400 F6000
            G60 S3
            """);
        using (timeline)
        await using (bench)
        {
            await RunToAsync(timeline, bench, 100);
            await PauseAsync(timeline, bench);

            double rest = await bench.Host.MachinePositionAsync(0);
            (double pausedX, _) = await bench.Host.RestorePointAsync(1);
            Assert.Multiple(() =>
            {
                Assert.That(pausedX, Is.EqualTo(rest).Within(0.05), "the restore point is where the head stopped");
                Assert.That(rest, Is.InRange(100.0, 399.0), "and the stop was inside the line, not at its end");
            });

            await ResumeToEndAsync(timeline, bench);
            Assert.That(bench.CanMaster.ScheduledSteps(driver: 0), Is.EqualTo(400 * 80),
                        "the interrupted line still finished exactly where it was going");
        }
    }

    /// <summary>
    /// A pause that lands after the file has been read to its end. The job outlives its reading, so
    /// this is an ordinary pause rather than a corner: the stop has nothing left to purge, and the
    /// resume has to carry on from a file that has already reached the end
    /// </summary>
    [Test]
    public async Task APauseAfterTheFileHasBeenReadToItsEnd()
    {
        (SteppedTimeline timeline, JobBench bench) = await StartAsync("""
            G90
            G1 X400 F6000
            G60 S3
            """);
        using (timeline)
        await using (bench)
        {
            // Far enough in that every code has been read and the job is only motion now
            await RunToAsync(timeline, bench, 200);
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.Status),
                        Is.EqualTo(MachineStatus.Processing), "the job outlives its reading");

            await PauseAsync(timeline, bench);
            await ResumeToEndAsync(timeline, bench);
            Assert.Multiple(async () =>
            {
                Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the job ended once");
                Assert.That(bench.CanMaster.ScheduledSteps(driver: 0), Is.EqualTo(400 * 80),
                            "having made the whole line rather than skipping what was left of it");
            });
        }
    }

    /// <summary>
    /// A job that defers codes, stopped and resumed. A Deferred-class code is dispatched without
    /// being awaited and waits for the move it was anchored to, so a stop has to say what becomes of
    /// the ones it leaves owed: their anchors will never retire, and the pause waits for them
    /// </summary>
    [Test]
    public async Task AStopSettlesWithDeferredCodesOwed()
    {
        (SteppedTimeline timeline, JobBench bench) = await StartAsync("""
            G90
            G1 X100 F6000
            M106 S1
            M106 S0.5
            G1 X400 F6000
            M107
            G60 S3
            """,
            configExtra: "M950 F0 C\"1.out3\" Q500");
        using (timeline)
        await using (bench)
        {
            await RunToAsync(timeline, bench, 200);
            await PauseAsync(timeline, bench);
            await ResumeToEndAsync(timeline, bench);

            Assert.Multiple(async () =>
            {
                Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1),
                            "the pause settled and the job ran to its end");
                Assert.That(bench.CanMaster.ScheduledSteps(driver: 0), Is.EqualTo(400 * 80));
            });
        }
    }

    /// <summary>
    /// Extrusion is an amount however the file expresses it, so a line that is already part done
    /// owes only the rest of it. Neither stop may leave a blob or a gap
    /// </summary>
    [Test]
    public async Task ExtrusionTotalsTheLineAcrossAPause()
    {
        (SteppedTimeline timeline, JobBench bench) = await StartAsync("""
            G90
            M83
            G1 X400 E40 F6000
            G60 S3
            """,
            configExtra: JobControlBench.OneTool);
        using (timeline)
        await using (bench)
        {
            await RunToAsync(timeline, bench, 150);
            await PauseAsync(timeline, bench);
            await ResumeToEndAsync(timeline, bench);

            Assert.Multiple(async () =>
            {
                Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((400.0, 0.0)), "the line finished");
                Assert.That(bench.CanMaster.ScheduledExtrusion(driver: 2), Is.EqualTo(40 * 420).Within(1e-2f),
                            "having extruded the whole 40 mm and no more");
            });
        }
    }

    /// <summary>
    /// Where the job is stopped, once per point. Concentrated in the first two lines because that is
    /// where the reader is still feeding the ring the last line of the file: the fault needs a job
    /// code part-way through going out when the stop lands, and by the second half of the job there
    /// is none. The later points are there to say that those stops are sound
    /// </summary>
    private static readonly double[] PausePoints =
    [
        10, 20, 30, 40, 50, 60, 70, 80, 90, 100,
        110, 120, 130, 140, 150, 160, 170, 180,
        210, 310
    ];

    /// <summary>
    /// The relative job of <c>restore-test-g91.gcode</c>, which pauses and resumes wrongly on real
    /// hardware. Four distances to travel, so a resume that re-reads the wrong line of them travels
    /// a whole 100 mm too far or too little rather than landing near where it should
    /// </summary>
    [Test]
    public Task ARelativeJobMakesItsDistanceFromEveryPausePoint()
        => EveryPausePointMakesTheWholeJobAsync("""
            G91
            G92 X0 Y0
            G1 X100 F6000
            G1 X100
            G1 X100
            G1 X100
            """);

    /// <summary>
    /// The same job written as absolute targets, which is <c>restore-test-g90.gcode</c>. A line here
    /// names where to be rather than how far to go, so a rewind to the wrong line is a move to the
    /// wrong place instead of a distance travelled twice - the same defect, differently expressed
    /// </summary>
    [Test]
    public Task AnAbsoluteJobEndsAtItsLastTargetFromEveryPausePoint()
        => EveryPausePointMakesTheWholeJobAsync("""
            G90
            G92 X0 Y0
            G1 X100 F6000
            G1 X200
            G1 X300
            G1 X400
            """);

    /// <summary>
    /// Pause and resume the job once per pause point, each on a bench of its own, and report every
    /// point that did not travel exactly the 400 mm the file describes
    /// </summary>
    /// <param name="job">The job file to run</param>
    /// <remarks>
    /// Swept rather than run once because the fault is a race and not a property of the position: the
    /// job's reader unwinds on the stop and rewinds the file itself, and whether it does so before
    /// the pause has told it where to rewind to decides whether the resume is right. One pause point
    /// therefore passes most times it is run, and only a sweep says whether the job can be stopped
    /// anywhere at all. Every point is collected before anything is asserted so that one failure does
    /// not hide the rest, which is what says whether a fix closed the race or moved it
    /// </remarks>
    private static async Task EveryPausePointMakesTheWholeJobAsync(string job)
    {
        List<string> wrong = [];
        foreach (double pausePoint in PausePoints)
        {
            (SteppedTimeline timeline, JobBench bench) = await StartAsync(job);
            using (timeline)
            await using (bench)
            {
                await RunToAsync(timeline, bench, pausePoint);
                await PauseAsync(timeline, bench);
                (double rest, _) = await bench.Host.RestorePointAsync(1);
                await ResumeToEndAsync(timeline, bench);

                // The path and not the endpoint, because a job of absolute targets ends at its last
                // one whatever order the lines were read in: re-reading a line the machine is already
                // past sends the head back and then forward again, which the position it finishes at
                // cannot show and the distance it travelled can
                double travelled = bench.CanMaster.MoveDistances().Sum();
                int steps = bench.CanMaster.ScheduledSteps(driver: 0);
                if (Math.Abs(travelled - 400.0) > 0.5 || steps != 400 * 80)
                {
                    wrong.Add($"asked to pause at {pausePoint} mm the machine stopped at {rest} mm, "
                              + $"travelled {travelled:F1} mm and finished at {steps / 80.0} mm");
                }
            }
        }

        Assert.That(wrong, Is.Empty, "every pause point must leave the job having travelled its whole 400 mm and no more");
    }
}
