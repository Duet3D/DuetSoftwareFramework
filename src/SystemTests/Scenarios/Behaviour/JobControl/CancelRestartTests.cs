using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace SystemTests.Scenarios.Behaviour.JobControl;

/// <summary>
/// Cancelling a paused job with M0, and restarting a job from a recorded file position with M26:
/// the existing half of power-fail resume. M26 S seeks the selected file; P (the fraction already
/// made) and C (the modal G command) are stored and applied only at M24. A restart from a file
/// offset does not run start.g, as RepRapFirmware's StartPrinting runs it only when starting from
/// the beginning (fromStart)
/// </summary>
[TestFixture]
[GCode("M0", "M24", "M26")]
public class CancelRestartTests : BenchFixture
{
    /// <summary>
    /// M0 while paused runs cancel.g, closes the file, and nothing after the pause point ever
    /// runs, not even after a later attempt to resume
    /// </summary>
    [Test]
    public async Task CancelWhilePausedRunsCancelMacro()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, "global after25 = 0\n", Machines.Xye.Homed)
                                             .WithSd(sd => sd.WriteGCode("job.gcode", """
                G90
                G1 X70 Y70 F6000
                M25
                set global.after25 = 1
                """)).StartAsync();

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);

        await bench.Host.ExecuteCodeAsync("M0");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("cancelRan"), Is.EqualTo(1), "cancel.g ran");
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.Zero, "stop.g did not: cancel.g exists");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Job.LastFileCancelled), Is.True,
                        "the job records itself as cancelled");
            Assert.That(await bench.Host.GlobalAsync("after25"), Is.Zero,
                        "nothing after the pause point ran: the file is closed");
        });

        Assert.That(await bench.Host.ExecuteCodeAsync("M24"),
                    Reply.IsError("Cannot print, because no file is selected!"),
                    "a resume after the cancel finds no file");
        Assert.That(await bench.Host.GlobalAsync("after25"), Is.Zero, "and runs nothing");
    }

    /// <summary>Without a cancel.g, cancelling a paused job falls back to stop.g</summary>
    [Test]
    public async Task CancelFallsBackToStopMacro()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd =>
        {
            File.Delete(Path.Combine(sd.Root, "sys", "cancel.g"));
            sd.WriteGCode("job.gcode", "G90\nG1 X70 Y70 F6000\nM25\n");
        }).StartAsync();

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        await bench.Host.ExecuteCodeAsync("M0");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1),
                        "stop.g ran as the fallback for the missing cancel.g");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Job.LastFileCancelled), Is.True,
                        "the job still records itself as cancelled");
        });
    }

    /// <summary>
    /// Eight segments of distinct lengths, so the capture says exactly which of them were made.
    /// The X65 line is bare: reading it needs a modal G1, which the restart's C parameter provides
    /// </summary>
    private const string SegmentsJob = "G90\nG1 X11 F6000\nG1 X23 F6000\nG1 X36 F6000\nG1 X50 F6000\nX65\nG1 X81 F6000\nG1 X98 F6000\nG1 X116 F6000\n";

    /// <summary>
    /// start.g with a move of its own. It counts its runs and moves, so a restart that does not run
    /// it leaves startRan at zero and its X5 move absent from the schedule
    /// </summary>
    private const string StartWithMove = "set global.startRan = global.startRan + 1\nG90\nG1 X5 Y5 F6000\n";

    /// <summary>The scheduled move distances, rounded to make the distinct segments recognisable</summary>
    private static double[] RoundedDistances(Bench bench)
        => bench.CanMaster.MoveDistances().Select(d => Math.Round((double)d, 1)).ToArray();

    /// <summary>
    /// M26 S restarts the selected file at a byte offset. A restart from an offset does not run
    /// start.g (RepRapFirmware's StartPrinting runs it only fromStart), and only the segments at or
    /// after the offset are made, measured from where the machine already is
    /// </summary>
    [Test]
    public async Task RestartFromFilePosition()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd =>
        {
            sd.WriteSys("start.g", StartWithMove);
            sd.WriteGCode("job.gcode", SegmentsJob);
        }).StartAsync();

        await bench.Host.ExecuteCodeAsync("M23 \"0:/gcodes/job.gcode\"");
        await bench.Host.ExecuteCodeAsync($"M26 S{SegmentsJob.IndexOf("G1 X81")}");
        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);

        double[] distances = RoundedDistances(bench);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("startRan"), Is.Zero,
                        "a restart from a file offset does not run start.g (RRF StartPrinting fromStart=false)");
            Assert.That(distances, Does.Contain(81.0),
                        "the segment at the offset runs from where the machine is (X0), not from a start.g move");
            Assert.That(distances, Does.Contain(17.0).And.Contain(18.0), "the segments after it");
            Assert.That(distances.Where(d => d is 11.0 or 12.0 or 13.0 or 14.0 or 15.0), Is.Empty,
                        "no segment before the offset was made");
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the restarted job ended normally");
        });
    }

    /// <summary>
    /// M26 C supplies the modal G command the resumed line is read under, so a bare coordinate line
    /// at the offset is a move. It has an effect only in CNC or Laser mode, where a bare line
    /// repeats the last G command (RepRapFirmware's <c>CodeParserBuffer</c>, <c>state.machineMode</c>);
    /// in FFF the bare X65 is not a move at all. The restart does not run start.g, so the move is
    /// measured from where the machine is
    /// </summary>
    [Test]
    public async Task RestartWithModalCommand()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, "M453", Machines.Xye.Homed)
                                             .WithSd(sd =>
        {
            sd.WriteSys("start.g", StartWithMove);
            sd.WriteGCode("job.gcode", SegmentsJob);
        }).StartAsync();

        await bench.Host.ExecuteCodeAsync("M23 \"0:/gcodes/job.gcode\"");
        await bench.Host.ExecuteCodeAsync($"M26 S{SegmentsJob.IndexOf("X65")} C1");
        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);

        double[] distances = RoundedDistances(bench);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("startRan"), Is.Zero,
                        "a restart from a file offset does not run start.g (RRF StartPrinting fromStart=false)");
            Assert.That(distances, Does.Contain(65.0),
                        "the bare X65 line was read as a G1 (C1's modal command in CNC mode) and moved from X0 to X65");
            Assert.That(distances, Does.Contain(16.0).And.Contain(17.0).And.Contain(18.0), "the segments after it");
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the restarted job ended normally");
        });
    }

    /// <summary>
    /// M26 P is the fraction of the line at the offset that has been made. What is left of an absolute
    /// line is the way from where the head is to its target, and the rest of its extrusion: whoever sets
    /// the fraction has moved the head to where the line was interrupted, as resurrect.g does
    /// </summary>
    /// <remarks>GCodes::DoStraightMove scales relative words, and ReadMove the extrusion of the first segment</remarks>
    [Test]
    public async Task RestartFractionSchedulesOnlyThePartNotYetMade()
    {
        const string job = "G91\nG1 X11 F6000\nG1 X65 E10\nG1 X81 E4\n";
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, "M563 P0 D0", "T0", Machines.Xye.Homed)
                                             .WithSd(sd => sd.WriteGCode("job.gcode", job)).StartAsync();

        await bench.Host.ExecuteCodeAsync("G90 G1 X32.5 F6000");
        await bench.Host.ExecuteCodeAsync("M83");
        await bench.Host.ExecuteCodeAsync("M400");
        bench.CanMaster.ClearCapture();

        await bench.Host.ExecuteCodeAsync("M23 \"0:/gcodes/job.gcode\"");
        await bench.Host.ExecuteCodeAsync($"M26 S{job.IndexOf("G1 X65")} P0.5");
        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);

        Assert.Multiple(async () =>
        {
            Assert.That(RoundedDistances(bench), Is.EqualTo(new[] { 32.5, 16.0 }), "X32.5 to X65, and X65 to X81");
            Assert.That(bench.CanMaster.ScheduledExtrusion(Machines.EDriver), Is.EqualTo((5 + 4) * Machines.EStepsPerMm).Within(1e-2f),
                        "half of E10, and all of E4: only the first move spends the fraction");
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the restarted job ended normally");
        });
    }

    /// <summary>
    /// What is left of a relative line is the rest of its distance. The G91 above the offset is not read:
    /// M24 gives the file the modes of the input that sends it (GCodeMachineState::CopyStateFrom)
    /// </summary>
    [Test]
    public async Task RestartFractionScalesARelativeLine()
    {
        const string job = "G91\nG1 X20\nG1 X30\n";
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd => sd.WriteGCode("job.gcode", job)).StartAsync();

        await bench.Host.ExecuteCodeAsync("M23 \"0:/gcodes/job.gcode\"");
        await bench.Host.ExecuteCodeAsync($"M26 S{job.IndexOf("G1 X20")} P0.25");
        await bench.Host.ExecuteCodeAsync("G91");
        await bench.Host.ExecuteCodeAsync("G1 F4800");
        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);

        Assert.Multiple(async () =>
        {
            Assert.That(RoundedDistances(bench), Is.EqualTo(new[] { 15.0, 30.0 }), "three quarters of X20, and all of X30");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Inputs[DuetAPI.CodeChannel.File]!.FeedRate),
                        Is.EqualTo(4800.0f), "the file has the feed rate too");
        });
    }

    /// <summary>What a job that made no move was owed is not taken from the next job</summary>
    [Test]
    public async Task RestartFractionThatWasNotUsedIsForgotten()
    {
        const string job = "G1 X10 F6000\nG4 P0\n";
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, "M563 P0 D0", "T0", "M83", Machines.Xye.Homed)
                                             .WithSd(sd =>
        {
            sd.WriteGCode("job.gcode", job);
            sd.WriteGCode("next.gcode", "M83\nG1 X20 E2 F6000\n");
        }).StartAsync();

        await bench.Host.ExecuteCodeAsync("M23 \"0:/gcodes/job.gcode\"");
        await bench.Host.ExecuteCodeAsync($"M26 S{job.IndexOf("G4")} P0.5");
        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/next.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);

        Assert.That(bench.CanMaster.ScheduledExtrusion(Machines.EDriver), Is.EqualTo(2 * Machines.EStepsPerMm).Within(1e-2f), "all of E2");
    }

    /// <summary>A job does not start in the inverse time mode the job before it ended in (GCodes::StartPrinting)</summary>
    [Test]
    public async Task AJobStartsOutOfInverseTimeMode()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd =>
        {
            sd.WriteGCode("job.gcode", "G93\n");
            sd.WriteGCode("next.gcode", "G1 X20 F6000\n");
        }).StartAsync();

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.That(await bench.Host.ReadModelAsync(model => model.Inputs[DuetAPI.CodeChannel.File]!.InverseTimeMode), Is.True, "nothing resets it at the end");
        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/next.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.That(await bench.Host.ReadModelAsync(model => model.Inputs[DuetAPI.CodeChannel.File]!.InverseTimeMode), Is.False);
    }

    /// <summary>A job started from the beginning of its file makes the first line whole and in its own modes</summary>
    /// <remarks>GCodes::StartPrinting clears restartMoveFractionDone if fromStart</remarks>
    [Test]
    public async Task RestartFractionIsIgnoredAtTheStartOfTheFile()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, "M563 P0 D0", "T0", Machines.Xye.Homed)
                                             .WithSd(sd => sd.WriteGCode("job.gcode", "G1 X20 E2 F6000\nG1 X30\n")).StartAsync();

        await bench.Host.ExecuteCodeAsync("M23 \"0:/gcodes/job.gcode\"");
        await bench.Host.ExecuteCodeAsync("M26 S0 P0.5");
        await bench.Host.ExecuteCodeAsync("G91");
        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("startRan"), Is.EqualTo(1), "start.g ran");
            Assert.That(RoundedDistances(bench), Is.EqualTo(new[] { 20.0, 10.0 }), "X0 to X20, and X20 to X30");
            Assert.That(bench.CanMaster.ScheduledExtrusion(Machines.EDriver), Is.EqualTo(2 * Machines.EStepsPerMm).Within(1e-2f),
                        "all of E2");
        });
    }
}
