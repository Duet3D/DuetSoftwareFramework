using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Heat;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace SystemTests.Scenarios.Behaviour.Moves;

/// <summary>
/// The movement lock between channels, asserted against RepRapFirmware's movement resource
/// (src/RepRapFirmware/src/GCodes/GCodes.cpp LockResource, UnlockAll): a code that waits for
/// standstill takes the lock before it waits and keeps it while its handler runs, including the
/// macros it runs, and a move from another channel waits for it. The long waits - a dwell and a
/// temperature wait - give their own hold back first, so that another channel can move meanwhile,
/// and starting or resuming the job suspends every hold of the channel while the File channel moves
/// </summary>
[TestFixture]
public class MovementLockTests : BenchFixture
{
    /// <summary>
    /// How long a code that is meant to be blocked is given to prove otherwise. Long enough for a
    /// move that is not blocked to be queued many times over
    /// </summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// A bed heater on board 1, added to the job control bench's X, Y and E machine
    /// </summary>
    private static readonly string HeatedBed = $"""
        M308 S0 P"{Expansion.Temp(Machines.DriverBoard, 0)}" Y"thermistor"
        M950 H0 C"{Expansion.Out(Machines.DriverBoard, 0)}" T0
        M140 H0
        """;

    /// <summary>
    /// A move from the daemon waits while G28 on another channel runs its homing macro, and is
    /// queued after the homing moves once G28 has finished
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 28 takes LockMovementSystemAndWaitForStandstill, and homex.g runs on a
    /// machine state pushed from G28's, which inherits its lockedResources, so the lock is not
    /// released until G28 itself completes. A G1 from any other channel fails LockMovement in the
    /// meantime and is retried. The M400 inside homex.g is a standstill code on the channel that
    /// already holds the lock, which takes it again rather than waiting for itself
    /// </remarks>
    [Test]
    public async Task AMoveFromAnotherChannelWaitsForHomingToFinish()
    {
        using SteppedTimeline timeline = new();
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .SteppedBy(timeline)
                                             .WithSd(sd =>
            sd.WriteSys("homex.g", "G91\nG1 H2 X5 F600\nM400\nG90\nG92 X0\n")).StartAsync();

        Task<string> home = bench.Host.ExecuteCodeAsync("G28 X");
        await bench.CanMaster.WaitUntilAsync(() => bench.CanMaster.MoveDistances().Contains(5.0f),
                                             what: "the homing move being queued");

        Task<string> daemonMove = bench.Host.ExecuteCodeAsync("G1 Y10 F600", CodeChannel.Daemon);
        await Task.Delay(Settle);
        Assert.Multiple(() =>
        {
            Assert.That(home.IsCompleted, Is.False, "G28 is still waiting for its homing move, the timeline being frozen");
            Assert.That(daemonMove.IsCompleted, Is.False, "the daemon's move waits while G28 holds the movement lock");
            Assert.That(bench.CanMaster.MoveDistances(), Does.Not.Contain(10.0f), "and is not queued in the middle of homing");
        });

        Assert.That((await timeline.WhileRunningAsync(() => home)).Trim(), Is.Empty, "G28 X homes X");
        await timeline.WhileRunningAsync(() => daemonMove);
        await timeline.RunUntilAsync(() => Task.FromResult(bench.CanMaster.MoveDistances().Contains(10.0f)),
                                     "the daemon's move being sent");

        float[] moves = bench.CanMaster.MoveDistances();
        Assert.That(Array.IndexOf(moves, 10.0f), Is.GreaterThan(Array.IndexOf(moves, 5.0f)),
                    "the daemon's move went out after the homing move");
    }

    /// <summary>
    /// A code that waits for standstill holds the lock from before it starts waiting, so a move
    /// from another channel cannot be queued while it waits and be waited for in its place, or land
    /// between the wait and the code acting on it
    /// </summary>
    /// <remarks>
    /// M400 is GCodes2.cpp case 400, LockCurrentMovementSystemAndWaitForStandstill. G4 on a
    /// channel that has commanded motion is GCodes.cpp DoDwell, which takes the same call before
    /// it dwells
    /// </remarks>
    [TestCase("M400", TestName = "A move from another channel waits for M400")]
    [TestCase("G4 P0", TestName = "A move from another channel waits for G4's standstill")]
    public async Task AMoveFromAnotherChannelWaitsForAStandstillCode(string code)
    {
        using SteppedTimeline timeline = new();
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .SteppedBy(timeline)
                                             .StartAsync();

        await timeline.WhileRunningAsync(() => bench.Host.ExecuteCodeAsync("G90"));
        await bench.Host.ExecuteCodeAsync("G1 X20 F600");

        Task<string> standstill = bench.Host.ExecuteCodeAsync(code);
        await Task.Delay(Settle);
        Task<string> daemonMove = bench.Host.ExecuteCodeAsync("G1 Y10 F600", CodeChannel.Daemon);
        await Task.Delay(Settle);
        Assert.Multiple(() =>
        {
            Assert.That(standstill.IsCompleted, Is.False, $"{code} is waiting for the X move, the timeline being frozen");
            Assert.That(daemonMove.IsCompleted, Is.False, $"the daemon's move waits while {code} holds the movement lock");
            Assert.That(bench.CanMaster.MoveDistances(), Does.Not.Contain(10.0f), $"and is not queued while {code} waits");
        });

        await timeline.WhileRunningAsync(() => standstill);
        await timeline.WhileRunningAsync(() => daemonMove);
        await timeline.RunUntilAsync(() => Task.FromResult(bench.CanMaster.MoveDistances().Contains(10.0f)),
                                     "the daemon's move being sent once the lock was released");
    }

    /// <summary>
    /// G28 that fails part-way through its homing macro releases the lock, so the next move from
    /// another channel is not left waiting for good
    /// </summary>
    /// <remarks>
    /// GCodes.cpp UnlockAll runs when a command completes however it completed, and clears
    /// toBeHomed when it releases an aborted G28's movement lock
    /// </remarks>
    [Test]
    public async Task AFailedHomingReleasesTheLock()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd =>
            sd.WriteSys("homex.g", "G91\nG1 H2 X5 F6000\nabort \"homing failed\"\n")).StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("G28 X");
        Assert.That(reply, Does.StartWith("Error"), "the macro's abort fails G28");

        await bench.Host.ExecuteCodeAsync("G1 Y10 F6000", CodeChannel.Daemon, timeoutMs: 5_000);
        await bench.CanMaster.WaitUntilAsync(() => bench.CanMaster.MoveDistances().Contains(10.0f),
                                             what: "the daemon's move being sent once G28 had ended");
    }

    /// <summary>
    /// A temperature wait gives the lock back once the machine has stopped, so another channel can
    /// move while the heaters come up
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp cases 116 and 190 take LockCurrentMovementSystemAndWaitForStandstill and then
    /// UnlockMovement, "allow babystepping and pausing while heating"
    /// </remarks>
    [TestCase("M116", TestName = "A move from another channel is not held up by M116")]
    [TestCase("M190 S60", TestName = "A move from another channel is not held up by M190")]
    public async Task ATemperatureWaitReleasesTheLock(string code)
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, JobMacros.Instrumented, HeatedBed, Machines.Xye.Homed);
        HeatManager heat = bench.Host.Services.GetRequiredService<HeatManager>();

        await bench.Host.ExecuteCodeAsync("M140 S60");
        bench.CanMaster.InjectHeatersStatus(srcAddress: Machines.DriverBoard, heaterNumber: 0, HeaterMode.Heating, currentTemperature: 25.0f);
        await bench.CanMaster.WaitUntilAsync(() => bench.Host.Model.Heat.Heaters[0] is { Current: > 20.0f and < 30.0f },
                                             what: "the heater report reaching the model");

        Task<string> wait = bench.Host.ExecuteCodeAsync(code);
        await bench.CanMaster.WaitUntilAsync(() => heat.IsWaitingForTemperatures, what: $"{code} blocking on the cold bed");

        await bench.Host.ExecuteCodeAsync("G1 Y10 F6000", CodeChannel.Daemon, timeoutMs: 5_000);
        await bench.CanMaster.WaitUntilAsync(() => bench.CanMaster.MoveDistances().Contains(10.0f),
                                             what: $"the daemon's move being sent while {code} was waiting");
        Assert.That(wait.IsCompleted, Is.False, $"{code} is still waiting for the bed");

        bench.CanMaster.InjectHeatersStatus(srcAddress: Machines.DriverBoard, heaterNumber: 0, HeaterMode.Stable, currentTemperature: 60.0f);
        await wait.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// A temperature wait in a tool change macro releases only its own hold, so the T-code's lock
    /// keeps moves from other channels out of the tool change until it is over
    /// </summary>
    /// <remarks>
    /// RepRapFirmware releases the T-code's lock here (GCodes2.cpp case 190, UnlockMovement), but
    /// keeps an outer lock for M109 since "Fix for 'Homing failed' if M109 is used in a homing
    /// file". DSF keeps it for every temperature wait, as rrf-differences.md records
    /// </remarks>
    [Test]
    public async Task ATemperatureWaitInAToolChangeKeepsTheToolChangesLock()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, HeatedBed + "\nM563 P0 D0\n", Machines.Xye.Homed)
                                             .WithSd(sd => sd.WriteSys("tpost0.g", "M190 S60\n"))
                                             .StartAsync();
        HeatManager heat = bench.Host.Services.GetRequiredService<HeatManager>();

        bench.CanMaster.InjectHeatersStatus(srcAddress: Machines.DriverBoard, heaterNumber: 0, HeaterMode.Heating, currentTemperature: 25.0f);
        await bench.CanMaster.WaitUntilAsync(() => bench.Host.Model.Heat.Heaters[0] is { Current: > 20.0f and < 30.0f },
                                             what: "the heater report reaching the model");

        Task<string> toolChange = bench.Host.ExecuteCodeAsync("T0");
        await bench.CanMaster.WaitUntilAsync(() => heat.IsWaitingForTemperatures, what: "tpost0.g's M190 blocking on the cold bed");

        Task<string> daemonMove = bench.Host.ExecuteCodeAsync("G1 Y10 F6000", CodeChannel.Daemon);
        await Task.Delay(Settle);
        Assert.Multiple(() =>
        {
            Assert.That(daemonMove.IsCompleted, Is.False, "the daemon's move waits while T0 holds the movement lock");
            Assert.That(bench.CanMaster.MoveDistances(), Does.Not.Contain(10.0f), "and is not queued in the middle of the tool change");
        });

        bench.CanMaster.InjectHeatersStatus(srcAddress: Machines.DriverBoard, heaterNumber: 0, HeaterMode.Stable, currentTemperature: 60.0f);
        Assert.That((await toolChange.WaitAsync(TimeSpan.FromSeconds(10))).Trim(), Is.Empty, "T0 completes once the bed is hot");
        await daemonMove.WaitAsync(TimeSpan.FromSeconds(5));
        await bench.CanMaster.WaitUntilAsync(() => bench.CanMaster.MoveDistances().Contains(10.0f),
                                             what: "the daemon's move being sent after the tool change");
    }

    /// <summary>
    /// M24 from a macro whose caller holds the lock resumes the job: M24 suspends the channel's
    /// holds while the File channel moves back to where it paused, and the caller completes after it
    /// </summary>
    /// <remarks>
    /// RepRapFirmware runs the resume on the channel that sent M24 (GCodes2.cpp case 24, DoResume
    /// from resuming1), so the caller's lock is the one its moves use. DSF resumes on the File
    /// channel, which needs the caller to give its lock up for the duration
    /// </remarks>
    [Test]
    public async Task ResumingFromAMacroWhoseCallerHoldsTheLock()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, "M563 P0 D0\n", Machines.Xye.Homed)
                                             .WithSd(sd =>
            {
                sd.WriteSys("tpost0.g", "M24\n");
                sd.WriteGCode("job.gcode", """
                    G90
                    G1 X100 Y50 F6000
                    M25
                    G1 X10 Y10 F6000
                    G60 S3
                    """);
            }).StartAsync();

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);

        string reply = await bench.Host.ExecuteCodeAsync("T0", timeoutMs: 20_000);
        Assert.That(reply.Trim(), Is.Empty, "T0 completes after tpost0.g's M24 resumed the job");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("resumeRan"), Is.EqualTo(1), "resume.g ran once");
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the job finished normally");
            Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((10.0, 10.0)), "at its final position");
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(0), "with tool 0 selected");
        });
    }
}
