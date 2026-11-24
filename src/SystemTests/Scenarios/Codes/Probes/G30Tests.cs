using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Motion;
using NUnit.Framework;
using static System.FormattableString;
using static SystemTests.Scenarios.Codes.Probes.ProbeSetup;

namespace SystemTests.Scenarios.Codes.Probes;

/// <summary>G30, probing a single point</summary>
/// <remarks>
/// <para>
/// RRF truth: <c>GCodes::ExecuteG30</c> (GCodes6.cpp) and the <c>probingAtPoint</c> states (GCodes4.cpp),
/// with the taps decided by <c>InitialiseTaps</c> and <c>CheckIfMoreTapsNeeded</c> (GCodes6.cpp) and the
/// heights by <c>ZProbe::GetStartingHeight</c> and <c>GetDiveHeight</c> (ZProbe.cpp). The regression
/// cases are <c>g30-single-probe</c>, <c>g30-report-stopped-height</c>, <c>g30-set-probe-trigger-height</c>,
/// <c>g30-set-tool-z-offset</c>, <c>g30-height-correction-and-probe-number</c>, <c>g30-probe-point</c>,
/// <c>g29-g30-retract-macro-position</c> and the G30 lines of <c>error-missing-parameters-probing-gcodes</c>.
/// The dive transforms (mesh, skew, a tool's axis map) are in <c>GeneratedMoveTransformTests</c>.
/// </para>
/// <para>
/// Unless a scenario says otherwise the machine is homed with the head at the start height, the
/// dive height above the trigger height above Z's minimum, which is where RepRapFirmware lifts to
/// after a tap. The bed is a fixed height in the machine's coordinates as they were when the code
/// started, and each dive is stopped at the time its profile reaches it, so every stopped height is
/// exact
/// </para>
/// </remarks>
[TestFixture]
[GCode("G30")]
public class G30Tests : BenchFixture
{
    private const byte ZDriver = Machines.ZDriver;
    private const float ZStepsPerMm = Machines.ZStepsPerMm;

    /// <summary>Z's minimum on <see cref="Machines.Xyz"/>, where every axis starts at 0</summary>
    private const float ZMin = 0.0f;

    /// <summary>The probe's M558 H, G31 Z and M558 F and T</summary>
    private const float DiveHeight = 5.0f, TriggerHeight = 1.5f;
    private const int ProbeSpeed = 300, TravelSpeed = 600;

    /// <summary>Where a tap starts from and lifts back to, <c>ZProbe::GetStartingHeight</c></summary>
    private const float StartHeight = ZMin + DiveHeight + TriggerHeight;

    /// <summary>Where a dive ends if nothing stops it on a homed Z (GCodes4.cpp probingAtPoint3)</summary>
    private const float DiveTarget = ZMin - DiveHeight + TriggerHeight;

    /// <summary>The steps of a dive from <see cref="StartHeight"/>, and of the lift back up from the trigger height</summary>
    private const int DiveSteps = (int)((DiveTarget - StartHeight) * ZStepsPerMm), LiftSteps = (int)(DiveHeight * ZStepsPerMm);

    /// <summary>Where the bed is, in the machine coordinates the code started with: 2.5 mm below the start height</summary>
    private const float Bed = 4.0f;

    /// <summary>Somewhere no dive reaches, so every dive runs to its end</summary>
    private const float NoBed = -1000.0f;

    /// <summary>RepRapFirmware's <c>MaxProbePoints</c> (Configuration.h), the number of G30 P points</summary>
    private const int MaxProbePoints = 32;

    /// <summary>A probe at 0 on the second input of the driver board, with the fixture's heights and speeds</summary>
    private static string ProbeConfig(string m558Extra = "")
        => Invariant($"{ProbeLine} H{DiveHeight} F{ProbeSpeed} T{TravelSpeed} {m558Extra}\nG31 K0 Z{TriggerHeight} P500");

    /// <summary>Counters a probe's deploy and retract macros add to, and the macros that do it</summary>
    private const string MacroCounters = "global deployRan = 0\nglobal retractRan = 0";

    private static Action<VirtualSd> CountingMacros(string suffix) => sd =>
    {
        sd.WriteSys($"deployprobe{suffix}.g", "set global.deployRan = global.deployRan + 1\n");
        sd.WriteSys($"retractprobe{suffix}.g", "set global.retractRan = global.retractRan + 1\n");
    };

    /// <summary>
    /// Start a bench with the fixture's probe, homed with the head at <see cref="StartHeight"/>
    /// </summary>
    private static async Task<Bench> StartHomedAsync(string m558Extra = "", string configExtra = "", Action<VirtualSd>? prepareSd = null)
    {
        Bench bench = await StartBenchAsync(ProbeConfig(m558Extra) + "\n" + configExtra, prepareSd);
        await bench.Host.ExecuteCodeAsync("G90");
        await bench.Host.ExecuteCodeAsync(Invariant($"G92 X0 Y0 Z{StartHeight}"));
        return bench;
    }

    /// <summary>Steps one move gives a driver of the driver board, zero for one it does not name</summary>
    private static int StepsOf(ScheduledMove move, byte driver) => move.DriverFor(Machines.DriverBoard, driver)?.Steps ?? 0;

    /// <summary>The probing moves scheduled since a count of moves, in order</summary>
    private static ScheduledMove[] Dives(Bench bench, int before)
        => bench.CanMaster.ScheduledMoves().Skip(before).Where(move => move.Has(ScheduleMoveFlags.CheckEndstops)).ToArray();

    /// <summary>Every move scheduled since a count of moves</summary>
    private static ScheduledMove[] MovesSince(Bench bench, int before) => bench.CanMaster.ScheduledMoves().Skip(before).ToArray();

    /// <summary>Whether every driver of a move stops when a probe on the driver board triggers</summary>
    private static bool ArmedOn(ScheduledMove move, int probeNumber)
        => move.Drivers.All(driver => driver.StopOnBoard == Machines.DriverBoard &&
                                      driver.StopOnHandle == RemoteProbes.HandleFor(probeNumber).All);

    /// <summary>Whether no driver of a move watches an endstop or a probe</summary>
    private static bool WatchesNothing(ScheduledMove move)
        => move.Drivers.All(driver => driver.StopOnBoard == ScheduleMovePacket.NoEndstopBoard);

    /// <summary>The ChangeInputMonitor messages sent since a count of them</summary>
    private static (byte Board, CanMessageChangeInputMonitorV1 Message)[] MonitorChanges(Bench bench, int before)
        => bench.CanMaster.CanMessages<CanMessageChangeInputMonitorV1>().Skip(before).ToArray();

    /// <summary>
    /// Run a probing code against a bed: every dive that reaches the bed is stopped where it does, and
    /// the probe is let go of again once the tap has been judged
    /// </summary>
    /// <param name="bench">The bench</param>
    /// <param name="code">The code, already started</param>
    /// <param name="before">The number of moves scheduled before the code started</param>
    /// <param name="startZ">Z's machine position when the code started</param>
    /// <param name="bed">Where the bed is for each tap, by its index, in the machine coordinates the code started with</param>
    /// <param name="probeNumber">The probe that triggers</param>
    /// <returns>The code's reply</returns>
    /// <remarks>
    /// Z is located from the steps of the moves since the code started, a stopped dive counting the
    /// steps it made before the stop, so the bed stays put however the code moves the head or
    /// redefines Z. A tap is judged before anything after it is queued, so
    /// the probe is released once the next move is scheduled or the code has finished
    /// </remarks>
    private static async Task<string> ProbeBedAsync(Bench bench, Task<string> code, int before, float startZ,
                                                     Func<int, float> bed, int probeNumber = 0)
    {
        int tapped = 0;
        Dictionary<uint, int> stoppedSteps = [];
        while (true)
        {
            await bench.CanMaster.WaitUntilAsync(() => code.IsCompleted || Dives(bench, before).Length > tapped, 60_000,
                                                 $"dive {tapped + 1} or the end of the code");
            ScheduledMove[] dives = Dives(bench, before);
            if (dives.Length <= tapped)
            {
                return (await code).Trim();
            }

            ScheduledMove dive = dives[tapped];
            float zAtStart = startZ + (MovesSince(bench, before).TakeWhile(move => move.MoveId != dive.MoveId)
                                                                .Sum(move => stoppedSteps.GetValueOrDefault(move.MoveId, StepsOf(move, ZDriver))) / ZStepsPerMm);
            float fraction = (zAtStart - bed(tapped)) / (Math.Abs(StepsOf(dive, ZDriver)) / ZStepsPerMm);
            tapped++;
            if (fraction > 1.0f)
            {
                continue;                       // this dive never reaches the bed and runs to its end
            }

            stoppedSteps[dive.MoveId] = (int)MathF.Round(fraction * StepsOf(dive, ZDriver));
            int movesBefore = bench.CanMaster.ScheduledMoves().Count;
            await TriggerProbeAtAsync(bench, dive, fraction * dive.Header.TotalDistance, probeNumber);
            await bench.CanMaster.WaitUntilAsync(() => code.IsCompleted || bench.CanMaster.ScheduledMoves().Count > movesBefore, 60_000,
                                                 $"tap {tapped} being judged");
            bench.CanMaster.InjectInputChange(Machines.DriverBoard, RemoteProbes.HandleFor(probeNumber), active: false);
        }
    }

    /// <summary>
    /// Run a probing code against a bed that is the same height for every tap, with Z's machine
    /// position <paramref name="startZ"/> when it starts
    /// </summary>
    private static async Task<string> ProbeBedAsync(Bench bench, string code, float bed, int probeNumber = 0, float startZ = StartHeight)
    {
        await WaitForClockSyncAsync(bench);
        int before = bench.CanMaster.ScheduledMoves().Count;
        return await ProbeBedAsync(bench, bench.Host.ExecuteCodeAsync(code, timeoutMs: 60_000), before, startZ, _ => bed, probeNumber);
    }

    /// <summary>
    /// Poll an object model value until it reaches the wanted value and return what it last read. A
    /// position published as a move winds down settles shortly after the code replies
    /// </summary>
    /// TODO generalise this
    private static async Task<double> SettledValueAsync(DcsTestHost host, Func<DuetControlServer.Model.ObjectModel, double?> read,
                                                        double expected, double tolerance = 1e-3, int timeoutMs = 2_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        double value;
        do
        {
            value = await host.ReadModelAsync(read) ?? double.NaN;
            if (Math.Abs(value - expected) <= tolerance)
            {
                return value;
            }
            await Task.Delay(25);
        }
        while (DateTime.UtcNow < deadline);
        return value;
    }

    /// <summary>Z's user and machine positions once they have settled at the expected value</summary>
    private static async Task<(double User, double Machine)> SettledZAsync(Bench bench, double expected)
        => (await SettledValueAsync(bench.Host, model => model.Move.Axes[2].UserPosition, expected),
            await SettledValueAsync(bench.Host, model => model.Move.Axes[2].MachinePosition, expected));

    // ---- G30 without P: setting Z ----------------------------------------------------------------

    /// <summary>
    /// Plain G30 probes down, records where the probe stopped, then redefines Z so that the point
    /// the probe stopped at reads as the trigger height, and homes Z
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes::ExecuteG30 (GCodes6.cpp) and the probingAtPoint states (GCodes4.cpp).
    /// SetLastStoppedHeight writes sensors.probes[0].lastStopHeight; for plain G30 the Z coordinate
    /// is reset to the trigger height with the height error zeroed and SetAxisIsHomed(Z_AXIS) is
    /// called, then the head moves back up to ZProbe::GetStartingHeight, which is the first dive
    /// height plus the trigger height. So the machine ends at dive height plus trigger height on the
    /// datum the probe just set: 5 + 1.5 here. X and Y are homed first, because GCodes2.cpp case 30
    /// refuses to probe until the kinematics' AxesToHomeBeforeProbing are. The probing move is closed
    /// by the controller's stop report and the probe judged triggered from its input, per
    /// rrf-differences.md section 2
    /// </remarks>
    [Test]
    public async Task G30SetsZDatumAndHomesZ()
    {
        await using Bench bench = await StartBenchAsync(ProbeLine + " F300\nG31 K0 Z1.5 P500");
        await bench.Host.ExecuteCodeAsync("G92 X0 Y0");

        Task<string> probeTask = bench.Host.ExecuteCodeAsync("G30");
        (uint MoveId, (byte Board, byte Driver)[] Drivers) dive =
            await WaitForArmedMoveAsync(bench.CanMaster, 0, "the probing move arriving armed on the probe");
        Assert.That(dive.Drivers, Is.EquivalentTo(new[] { ((byte)1, (byte)2) }),
                    "the probing move arms driver 1.2, which is Z");

        // The input closes and the controller stops the move: both reports, in that order, so the
        // probe reads triggered when the tap is judged
        bench.CanMaster.InjectInputChange(Machines.DriverBoard, RemoteProbes.HandleFor(0), active: true);
        StopMove(bench.CanMaster, dive);

        string reply = await probeTask;
        Assert.That(reply.Trim(), Is.Empty, "G30 completed without error");
        bool homed = await bench.Host.ReadModelAsync(model => model.Move.Axes[2].Homed);
        const double diveHeight = 5.0, triggerHeight = 1.5;
        double zPosition = await SettledValueAsync(bench.Host, model => model.Move.Axes[2].MachinePosition,
                                                   diveHeight + triggerHeight, 0.01);
        float lastStopHeight = await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.LastStopHeight);
        Assert.Multiple(() =>
        {
            Assert.That(homed, Is.True,
                        "plain G30 homes Z (RRF GCodes4.cpp, SetAxisIsHomed(Z_AXIS))");
            Assert.That(zPosition, Is.EqualTo(diveHeight + triggerHeight).Within(0.01),
                        "plain G30 sets the Z datum from the probe, so the head reads the dive height above the trigger height (RRF GCodes4.cpp probingAtPoint4/5, ZProbe::GetStartingHeight)");
            Assert.That(lastStopHeight, Is.GreaterThan(0.0f),
                        "G30 records where the probe stopped in sensors.probes[0].lastStopHeight (RRF SetLastStoppedHeight)");
        });
    }

    /// <summary>
    /// Plain G30 on a homed machine: one dive armed on the probe, the probe's board told to report
    /// quickly for its duration, Z redefined so the stop reads as the trigger height, and a lift of the
    /// dive height
    /// </summary>
    /// <remarks>
    /// RRF truth: probingAtPoint3 dives to <c>AxisMinimum(Z) - GetDiveHeight(-1) + trigger height</c> on
    /// a homed Z, after <c>RemoteZProbe::SetProbing(true)</c> sends ChangeInputMonitor
    /// changeMinInterval 2 (ActiveProbeReportInterval) and before SetProbing(false) sends 25
    /// (InactiveProbeReportInterval); probingAtPoint4 sets Z to the trigger height and homes it, and
    /// probingAtPoint4a lifts to <c>GetStartingHeight</c>. <c>g30-single-probe</c> records the two
    /// changeInputMonitorV1 frames, action 4 with param 2 then 25, on the probe's handle
    /// </remarks>
    [Test]
    public async Task G30SetsZToTheTriggerHeightWhereTheProbeStopped()
    {
        await using Bench bench = await StartHomedAsync();
        int before = bench.CanMaster.ScheduledMoves().Count;
        int monitorsBefore = bench.CanMaster.CanMessages<CanMessageChangeInputMonitorV1>().Count;

        string reply = await ProbeBedAsync(bench, "G30", Bed);
        await bench.Host.ExecuteCodeAsync("M400");

        ScheduledMove[] moves = MovesSince(bench, before);
        (byte Board, CanMessageChangeInputMonitorV1 Message)[] monitors = MonitorChanges(bench, monitorsBefore);
        (double user, double machine) = await SettledZAsync(bench, StartHeight);
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Is.Empty);
            Assert.That(moves, Has.Length.EqualTo(2), "the dive and the lift");
            Assert.That(moves.Select(move => move.Has(ScheduleMoveFlags.CheckEndstops)), Is.EqualTo(new[] { true, false }));
            Assert.That(ArmedOn(moves[0], 0), Is.True, "every driver of the dive stops on probe 0");
            Assert.That(moves[0].Drivers.Where(driver => driver.Steps != 0).Select(driver => (driver.BoardAddress, driver.DriverNumber)),
                        Is.EqualTo(new[] { (Machines.DriverBoard, ZDriver) }), "the dive moves Z alone");
            Assert.That(StepsOf(moves[0], ZDriver), Is.EqualTo(DiveSteps), "from the start height to the dive height below Z's minimum");
            Assert.That(WatchesNothing(moves[1]), Is.True, "the lift watches nothing");
            Assert.That(StepsOf(moves[1], ZDriver), Is.EqualTo(LiftSteps), "the lift is the dive height");

            Assert.That(monitors.Select(change => change.Board), Has.All.EqualTo(Machines.DriverBoard));
            Assert.That(monitors.Select(change => change.Message.Handle.All), Has.All.EqualTo(RemoteProbes.HandleFor(0).All));
            Assert.That(monitors.Select(change => change.Message.Action), Has.All.EqualTo(CanMessageChangeInputMonitorV1.ActionChangeMinInterval),
                        "a digital probe has no threshold to send");
            Assert.That(monitors.Select(change => change.Message.Param),
                        Is.EqualTo(new[] { ProbeArming.ActiveReportInterval, ProbeArming.InactiveReportInterval }),
                        "fast reports for the dive, then back to the idle rate");

            Assert.That(user, Is.EqualTo(StartHeight).Within(1e-3), "the stop reads as the trigger height, and the lift adds the dive height");
            Assert.That(machine, Is.EqualTo(StartHeight).Within(1e-3));
            Assert.That(await bench.Host.ReadAxisAsync('Z', axis => axis.Homed), Is.True);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.LastStopHeight), Is.EqualTo(Bed).Within(1e-3f),
                        "sensors.probes[0].lastStopHeight is where the probe stopped, on the old datum");
        });
    }

    /// <summary>
    /// Plain G30 with a tool selected sets the machine's Z, so the tool's Z offset still applies to
    /// what the user position reads
    /// </summary>
    /// <remarks>
    /// RRF truth: probingAtPoint4 writes the trigger height into <c>ms.raw.coords</c>, which are machine
    /// coordinates, and then <c>ToolOffsetInverseTransform</c>, so the user Z is the machine Z plus the
    /// tool's Z offset (GCodes::ToolOffsetInverseTransform subtracts the negated offset)
    /// </remarks>
    [Test]
    public async Task G30SetsTheMachinesZNotTheToolsZ()
    {
        const float toolZOffset = -2.0f;
        await using Bench bench = await StartHomedAsync();
        await bench.Host.ExecuteCodeAsync("M563 P0 S\"probe-tool\"");
        await bench.Host.ExecuteCodeAsync(Invariant($"G10 P0 Z{toolZOffset}"));
        await bench.Host.ExecuteCodeAsync("T0 P0");
        int before = bench.CanMaster.ScheduledMoves().Count;

        string reply = await ProbeBedAsync(bench, "G30", Bed);
        await bench.Host.ExecuteCodeAsync("M400");

        ScheduledMove[] moves = MovesSince(bench, before);
        double machine = await SettledValueAsync(bench.Host, model => model.Move.Axes[2].MachinePosition, StartHeight);
        double user = await SettledValueAsync(bench.Host, model => model.Move.Axes[2].UserPosition, StartHeight + toolZOffset);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Is.Empty);
            Assert.That(moves.Select(move => StepsOf(move, ZDriver)), Is.EqualTo(new[] { DiveSteps, LiftSteps }),
                        "the dive and the lift, measured in the machine's coordinates");
            Assert.That(machine, Is.EqualTo(StartHeight).Within(1e-3), "the machine's Z is the one set to the trigger height");
            Assert.That(user, Is.EqualTo(StartHeight + toolZOffset).Within(1e-3), "and the tool's offset is applied on top of it");
        });
    }

    /// <summary>
    /// A probe that never triggers fails G30 without touching Z's datum or the last stopped height
    /// </summary>
    /// <remarks>
    /// RRF truth: probingAtPoint4 sets "Probe was not triggered during probing move" and zeroes the
    /// height error, skips the datum reset (<c>!hadProbingError</c>) and leaves lastStopHeight alone.
    /// <c>g30-single-probe</c> records the error with the dive run to its end
    /// </remarks>
    [Test]
    public async Task G30WithTheProbeNeverTriggeringFails()
    {
        await using Bench bench = await StartHomedAsync();
        int before = bench.CanMaster.ScheduledMoves().Count;

        string reply = await ProbeBedAsync(bench, "G30", NoBed);
        await bench.Host.ExecuteCodeAsync("M400");

        ScheduledMove[] moves = MovesSince(bench, before);
        double expected = StartHeight + (moves.Sum(move => StepsOf(move, ZDriver)) / ZStepsPerMm);
        (double user, double machine) = await SettledZAsync(bench, expected);
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain("not triggered"));
            Assert.That(StepsOf(moves[0], ZDriver), Is.EqualTo(DiveSteps), "the dive runs to its end");
            Assert.That(machine, Is.EqualTo(expected).Within(1e-3), "Z reads where the motors put it: its datum is not touched");
            Assert.That(user, Is.EqualTo(expected).Within(1e-3));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.LastStopHeight), Is.Zero,
                        "nothing was stopped at");
        });
    }

    /// <summary>A probe that is already triggered refuses G30 before anything moves</summary>
    /// <remarks>
    /// RRF truth: probingAtPoint3 checks <c>zp->Stopped()</c> before <c>EnableZProbe</c> and
    /// <c>SetProbing</c>, so the board is told nothing; it sets "probe already triggered before probing
    /// move started" and retracts the probe it deployed in ExecuteG30. Nothing moves, because G30
    /// without P dives from where the head is
    /// </remarks>
    [Test]
    public async Task G30WithTheProbeAlreadyTriggeredIsRefusedBeforeMoving()
    {
        await using Bench bench = await StartHomedAsync(configExtra: MacroCounters, prepareSd: CountingMacros("0"));
        bench.CanMaster.InjectInputChange(Machines.DriverBoard, RemoteProbes.HandleFor(0), active: true);
        await bench.Host.WaitForModelAsync(model => model.Sensors.Probes[0]!.Value.FirstOrDefault(), value => value >= 500,
                                           "probe 0 reading triggered");
        int before = bench.CanMaster.ScheduledMoves().Count;
        int monitorsBefore = bench.CanMaster.CanMessages<CanMessageChangeInputMonitorV1>().Count;

        string reply = (await bench.Host.ExecuteCodeAsync("G30")).Trim();

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain("already triggered"));
            Assert.That(MovesSince(bench, before), Is.Empty, "nothing moves");
            Assert.That(MonitorChanges(bench, monitorsBefore), Is.Empty, "the board is not told about a tap that does not happen");
            Assert.That(await bench.Host.GlobalAsync("deployRan"), Is.EqualTo(1), "the probe was deployed");
            Assert.That(await bench.Host.GlobalAsync("retractRan"), Is.EqualTo(1), "and is retracted again");
            Assert.That(await bench.Host.ReadAxisAsync('Z', axis => axis.UserPosition), Is.EqualTo(StartHeight).Within(1e-3f));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.LastStopHeight), Is.Zero);
        });
    }

    /// <summary>A board that refuses to watch the probe fails G30 before the dive</summary>
    /// <remarks>
    /// RRF truth: probingAtPoint3, where <c>RemoteZProbe::SetProbing(true)</c> returning false (the
    /// board's text is reported as a message) sets "failed to enable probe" and retracts the probe
    /// without queueing the dive
    /// </remarks>
    [Test]
    public async Task G30WithTheBoardRefusingToWatchTheProbeFailsWithoutDiving()
    {
        const string boardSays = "Board 1 does not have input handle 3000";
        await using Bench bench = await StartHomedAsync(configExtra: MacroCounters, prepareSd: CountingMacros("0"));
        bench.CanMaster.ReportWith<CanMessageChangeInputMonitorV1>(boardSays, CodeResult.Error);
        int before = bench.CanMaster.ScheduledMoves().Count;

        string reply = (await bench.Host.ExecuteCodeAsync("G30")).Trim();

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain(boardSays), "the board's refusal fails the code");
            Assert.That(MovesSince(bench, before), Is.Empty, "nothing moves");
            Assert.That(await bench.Host.GlobalAsync("retractRan"), Is.EqualTo(1), "the probe is retracted");
            Assert.That(await bench.Host.ReadAxisAsync('Z', axis => axis.UserPosition), Is.EqualTo(StartHeight).Within(1e-3f));
            Assert.That(await bench.Host.ReadAxisAsync('Z', axis => axis.Homed), Is.True);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.LastStopHeight), Is.Zero);
        });
    }

    /// <summary>A board that never answers when told to watch the probe fails G30 before the dive</summary>
    /// <remarks>
    /// RRF truth: the ChangeInputMonitor of <c>RemoteZProbe::SetProbing(true)</c> times out as
    /// <c>canResponseTimeout</c> ("CAN response timeout: board ..."), which is not ok, so probingAtPoint3
    /// fails the code with "failed to enable probe" as for a refusal
    /// </remarks>
    [Test]
    public async Task G30WithTheBoardNeverAnsweringFailsWithoutDiving()
    {
        await using Bench bench = await StartHomedAsync();
        bench.CanMaster.NeverAnswer<CanMessageChangeInputMonitorV1>();
        int before = bench.CanMaster.ScheduledMoves().Count;

        string reply = (await bench.Host.ExecuteCodeAsync("G30")).Trim();

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain("CAN response timeout"));
            Assert.That(MovesSince(bench, before), Is.Empty, "nothing moves");
            Assert.That(await bench.Host.ReadAxisAsync('Z', axis => axis.UserPosition), Is.EqualTo(StartHeight).Within(1e-3f));
        });
    }

    /// <summary>
    /// A board that warns when told to watch the probe has still done it, so G30 probes and passes the
    /// warning on
    /// </summary>
    /// <remarks>
    /// RepRapFirmware is the outlier here: <c>RemoteZProbe::SetProbing</c> succeeds only on
    /// <c>GCodeResult::ok</c>, so a warning fails the code with "failed to enable probe", where
    /// everywhere else a warning means the request was carried out (CANlib's <c>Succeeded()</c>). This
    /// pins the convention
    /// </remarks>
    [Test]
    public async Task G30WithTheBoardWarningWhenToldToWatchTheProbeStillProbes()
    {
        const string boardSays = "probe input is noisy";
        await using Bench bench = await StartHomedAsync();
        bench.CanMaster.ReportWith<CanMessageChangeInputMonitorV1>(boardSays, CodeResult.Warning);

        string reply = await ProbeBedAsync(bench, "G30", Bed);

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.Not.StartWith("Error"));
            Assert.That(await bench.Host.WaitForMessageAsync(MessageType.Warning, boardSays), Does.Contain(boardSays),
                        "the warning is passed on");
            Assert.That(await bench.Host.ReadAxisAsync('Z', axis => axis.Homed), Is.True, "and the probe set Z");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.LastStopHeight), Is.EqualTo(Bed).Within(1e-3f));
        });
    }

    /// <summary>
    /// G30 runs the probe's own deploy macro before the dive and its retract macro after, once each
    /// </summary>
    /// <remarks>
    /// RRF truth: ExecuteG30 calls <c>DeployZProbe</c>, which runs <c>deployprobe&lt;K&gt;.g</c>, and
    /// probingAtPoint5 calls <c>RetractZProbe</c>, which runs <c>retractprobe&lt;K&gt;.g</c>
    /// (GCodes3.cpp)
    /// </remarks>
    [Test]
    public async Task G30RunsTheProbesMacrosAroundTheDive()
    {
        await using Bench bench = await StartHomedAsync(configExtra: MacroCounters, prepareSd: CountingMacros("0"));
        await WaitForClockSyncAsync(bench);
        int before = bench.CanMaster.ScheduledMoves().Count;

        Task<string> code = bench.Host.ExecuteCodeAsync("G30");
        await bench.CanMaster.WaitUntilAsync(() => Dives(bench, before).Length > 0, 20_000, "the dive");
        int deployedAtDive = await bench.Host.GlobalAsync("deployRan"), retractedAtDive = await bench.Host.GlobalAsync("retractRan");
        string reply = await ProbeBedAsync(bench, code, before, StartHeight, _ => Bed);

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Is.Empty);
            Assert.That((deployedAtDive, retractedAtDive), Is.EqualTo((1, 0)), "deployed before the dive");
            Assert.That(await bench.Host.GlobalAsync("deployRan"), Is.EqualTo(1), "once");
            Assert.That(await bench.Host.GlobalAsync("retractRan"), Is.EqualTo(1), "and retracted once afterwards");
        });
    }

    /// <summary>
    /// With M558 A3 and two taps that agree, G30 stops after the second
    /// </summary>
    /// <remarks>
    /// RRF truth: CheckIfMoreTapsNeeded accepts the reading once two consecutive height errors differ
    /// by no more than the tolerance (M558 S), and averages those two
    /// </remarks>
    [Test]
    public async Task G30StopsTappingWhenTwoReadingsAgree()
    {
        await using Bench bench = await StartHomedAsync("A3 S0.03");
        int before = bench.CanMaster.ScheduledMoves().Count;

        string reply = await ProbeBedAsync(bench, "G30", Bed);

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Is.Empty);
            Assert.That(Dives(bench, before), Has.Length.EqualTo(2), "the second tap agrees with the first");
            Assert.That(await bench.Host.ReadAxisAsync('Z', axis => axis.Homed), Is.True);
        });
    }

    // ---- S parameter -------------------------------------------------------------------------------

    /// <summary>
    /// G30 S-1 reports the height the probe stopped at, and leaves an unhomed Z unhomed
    /// </summary>
    /// <remarks>
    /// RRF truth: probingAtPoint7 replies "Stopped at height %.3f mm" from GetLastStoppedHeight, and
    /// S-1 never reaches the SetAxisIsHomed of plain G30 (probingAtPoint4). <c>g30-report-stopped-height</c>
    /// </remarks>
    [Test]
    public async Task G30S1ReportsTheStoppedHeightAndLeavesZUnhomed()
    {
        const float bed = -2.5f;
        await using Bench bench = await StartBenchAsync(ProbeConfig());
        await bench.Host.ExecuteCodeAsync("G90");
        await bench.Host.ExecuteCodeAsync("G92 X0 Y0");

        string reply = await ProbeBedAsync(bench, "G30 S-1", bed, startZ: 0.0f);

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Is.EqualTo(Invariant($"Stopped at height {bed:F3} mm")));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.LastStopHeight), Is.EqualTo(bed).Within(1e-3f));
            Assert.That(await bench.Host.ReadAxisAsync('Z', axis => axis.Homed), Is.False, "S-1 only measures");
        });
    }

    /// <summary>G30 S-1 with H reports the stopped height less H</summary>
    /// <remarks>
    /// RRF truth: probingAtPoint4 records <c>m[Z_AXIS] - g30HValue</c> as the stopped height.
    /// <c>g30-height-correction-and-probe-number</c> passes H but never triggers, so it does not reach this
    /// </remarks>
    [Test]
    public async Task G30HIsTakenOffTheStoppedHeight()
    {
        const float h = 0.3f;
        await using Bench bench = await StartHomedAsync();

        string reply = await ProbeBedAsync(bench, Invariant($"G30 S-1 H{h}"), Bed);

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Is.EqualTo(Invariant($"Stopped at height {Bed - h:F3} mm")));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.LastStopHeight), Is.EqualTo(Bed - h).Within(1e-3f));
        });
    }

    /// <summary>
    /// A plain single-tap G30 with H sets Z to the trigger height where the probe stopped, as without H;
    /// H is only taken off the recorded stopped height
    /// </summary>
    /// <remarks>
    /// RRF truth: probingAtPoint4 records <c>m[Z_AXIS] - g30HValue</c> as the stopped height, but the first
    /// tap of a plain G30 (<c>tapsDone &lt;= 1</c>) then sets Z to <c>GetActiveModeTriggerHeight()</c> and
    /// zeroes the height error, so H does not reach Z. The wiki describes H as moving the datum; the source
    /// is what this pins
    /// </remarks>
    [Test]
    public async Task G30HDoesNotMoveTheDatumSetByTheFirstTap()
    {
        const float h = 0.3f;
        await using Bench bench = await StartHomedAsync();

        string reply = await ProbeBedAsync(bench, Invariant($"G30 H{h}"), Bed);
        await bench.Host.ExecuteCodeAsync("M400");

        (double user, double machine) = await SettledZAsync(bench, StartHeight);
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Is.Empty);
            Assert.That(user, Is.EqualTo(StartHeight).Within(1e-3), "the stop reads as the trigger height, H or not");
            Assert.That(machine, Is.EqualTo(StartHeight).Within(1e-3));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.LastStopHeight), Is.EqualTo(Bed - h).Within(1e-3f),
                        "the stopped height has H taken off");
        });
    }

    /// <summary>
    /// With more than one tap, H is taken off every later tap's height error, so it moves the datum the
    /// accepted reading sets
    /// </summary>
    /// <remarks>
    /// RRF truth: the first tap sets Z to the trigger height with a zero height error (probingAtPoint4). The
    /// second stops at the trigger height of that datum, and its height error is
    /// <c>(trigger - H) - trigger = -H</c>. CheckIfMoreTapsNeeded accepts the two, which differ by H, within
    /// the tolerance and averages them to <c>-H/2</c>; probingAtPoint4a lifts to <c>GetStartingHeight</c> and
    /// probingAtPoint5 subtracts the error, so Z reads H/2 above the start height
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task G30HMovesTheDatumThroughTheLaterTaps()
    {
        const float h = 0.3f;
        await using Bench bench = await StartHomedAsync("A2 S1");
        int before = bench.CanMaster.ScheduledMoves().Count;

        string reply = await ProbeBedAsync(bench, Invariant($"G30 H{h}"), Bed);
        await bench.Host.ExecuteCodeAsync("M400");

        const double expected = StartHeight + (h / 2.0f);
        (double user, _) = await SettledZAsync(bench, expected);
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Is.Empty);
            Assert.That(Dives(bench, before), Has.Length.EqualTo(2), "the two readings agree within the tolerance");
            Assert.That(user, Is.EqualTo(expected).Within(1e-3), "the mean of the errors 0 and -H is taken off Z");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.LastStopHeight), Is.EqualTo(TriggerHeight - h).Within(1e-3f),
                        "the second tap stopped at the trigger height of the first tap's datum, less H");
        });
    }

    /// <summary>A G30 S-1 whose probe never triggers leaves the head at the end of the dive</summary>
    /// <remarks>
    /// RRF truth: S-1 goes from probingAtPoint4 straight to probingAtPoint7, with no lift, whatever the
    /// tap's outcome. <c>g30-report-stopped-height</c> ends at Z −101, the end of its dive
    /// </remarks>
    [Test]
    public async Task G30S1WithTheProbeNeverTriggeringLeavesTheHeadAtTheEndOfTheDive()
    {
        await using Bench bench = await StartHomedAsync();
        int before = bench.CanMaster.ScheduledMoves().Count;

        string reply = await ProbeBedAsync(bench, "G30 S-1", NoBed);
        await bench.Host.ExecuteCodeAsync("M400");

        ScheduledMove[] moves = MovesSince(bench, before);
        (double user, _) = await SettledZAsync(bench, DiveTarget);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain("not triggered"));
            Assert.That(moves.Select(move => StepsOf(move, ZDriver)), Is.EqualTo(new[] { DiveSteps }), "the dive alone");
            Assert.That(user, Is.EqualTo(DiveTarget).Within(1e-3));
        });
    }

    /// <summary>
    /// G30 S-3 writes the height the probe stopped at into the probe's trigger height
    /// </summary>
    /// <remarks>
    /// RRF truth: probingAtPoint7 calls <c>SetTriggerHeight(GetLastStoppedHeight())</c>, which is
    /// <c>offsets[Z_AXIS] = -height</c> and so <c>triggerHeight</c> in the object model, and replies
    /// "Z probe trigger height set to %.3f mm". Z is neither redefined nor homed by S-3.
    /// <c>g30-set-probe-trigger-height</c> brackets it with G31 reports
    /// </remarks>
    [Test]
    public async Task G30S3SetsTheTriggerHeightToTheStoppedHeight()
    {
        await using Bench bench = await StartHomedAsync();
        int before = bench.CanMaster.ScheduledMoves().Count;

        string reply = await ProbeBedAsync(bench, "G30 S-3", Bed);
        await bench.Host.ExecuteCodeAsync("M400");
        string report = await bench.Host.ExecuteCodeAsync("G31 K0");

        double expected = Bed + (MovesSince(bench, before).SkipWhile(move => !move.Has(ScheduleMoveFlags.CheckEndstops)).Skip(1)
                                                          .Sum(move => StepsOf(move, ZDriver)) / ZStepsPerMm);
        (double user, _) = await SettledZAsync(bench, expected);
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Is.EqualTo(Invariant($"Z probe trigger height set to {Bed:F3} mm")));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.TriggerHeight), Is.EqualTo(Bed).Within(1e-3f),
                        "sensors.probes[0].triggerHeight");
            Assert.That(report, Does.Contain(Invariant($"trigger height {Bed:F3}")), "G31 reports it");
            Assert.That(user, Is.EqualTo(expected).Within(1e-3), "Z keeps its datum: it reads the stopped height plus whatever moved it since");
        });
    }

    /// <summary>A G30 S-3 whose probe never triggers leaves the trigger height alone</summary>
    /// <remarks>
    /// <c>g30-set-probe-trigger-height</c> reads trigger height 0.000 either side of the failed probe.
    /// RepRapFirmware's probingAtPoint7 does call <c>SetTriggerHeight(GetLastStoppedHeight())</c> even
    /// after the error, which only leaves the trigger height alone when no earlier probe stopped (it
    /// writes the stale height of the last one that did); a failed probe changing nothing is what
    /// plain G30 and G30 P do, and is what this pins
    /// </remarks>
    [Test]
    public async Task G30S3WithTheProbeNeverTriggeringLeavesTheTriggerHeight()
    {
        await using Bench bench = await StartHomedAsync();
        await ProbeBedAsync(bench, "G30 S-1", Bed);
        await bench.Host.ExecuteCodeAsync(Invariant($"G92 Z{StartHeight}"));

        string reply = await ProbeBedAsync(bench, "G30 S-3", NoBed);

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain("not triggered"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.TriggerHeight), Is.EqualTo(TriggerHeight).Within(1e-3f));
        });
    }

    /// <summary>G30 S-2 with no tool selected is refused before anything happens</summary>
    /// <remarks>
    /// RRF truth: ExecuteG30's first check, "G30 S-2 commanded with no tool selected", before the probe
    /// is looked up or deployed. <c>g30-set-tool-z-offset</c> and <c>error-missing-parameters-probing-gcodes</c>
    /// </remarks>
    [Test]
    public async Task G30S2WithNoToolSelectedIsRefused()
    {
        await using Bench bench = await StartHomedAsync(configExtra: MacroCounters, prepareSd: CountingMacros("0"));
        int before = bench.CanMaster.ScheduledMoves().Count;
        int monitorsBefore = bench.CanMaster.CanMessages<CanMessageChangeInputMonitorV1>().Count;

        string reply = (await bench.Host.ExecuteCodeAsync("G30 S-2")).Trim();

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error"));
            Assert.That(MovesSince(bench, before), Is.Empty);
            Assert.That(MonitorChanges(bench, monitorsBefore), Is.Empty);
            Assert.That(await bench.Host.GlobalAsync("deployRan"), Is.Zero, "the probe is not deployed");
        });
    }

    // ---- K -----------------------------------------------------------------------------------------

    /// <summary>G30 K probes with the probe it names, on that probe's input</summary>
    /// <remarks>
    /// RRF truth: GCodes::SetZProbeNumber. <c>g30-height-correction-and-probe-number</c> records each
    /// probe's changeInputMonitorV1 frames on its own handle
    /// </remarks>
    [Test]
    public async Task G30KSelectsTheProbe()
    {
        string secondProbe = Invariant($"M558 K1 P8 C\"{Expansion.GpioIn(Machines.DriverBoard, 2)}\" H{DiveHeight} F{ProbeSpeed} T{TravelSpeed}\nG31 K1 Z{TriggerHeight} P500");
        await using Bench bench = await StartHomedAsync(configExtra: secondProbe);
        int before = bench.CanMaster.ScheduledMoves().Count;
        int monitorsBefore = bench.CanMaster.CanMessages<CanMessageChangeInputMonitorV1>().Count;

        string reply = await ProbeBedAsync(bench, "G30 K1 S-1", Bed, probeNumber: 1);

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Is.EqualTo(Invariant($"Stopped at height {Bed:F3} mm")));
            Assert.That(Dives(bench, before).Select(dive => ArmedOn(dive, 1)), Has.All.True, "the dive stops on probe 1");
            Assert.That(MonitorChanges(bench, monitorsBefore).Select(change => change.Message.Handle.All).ToArray(),
                        Has.Length.EqualTo(2).And.All.EqualTo(RemoteProbes.HandleFor(1).All), "probe 1's board is told");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[1]!.LastStopHeight), Is.EqualTo(Bed).Within(1e-3f));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.LastStopHeight), Is.Zero, "probe 0 is not used");
        });
    }

    /// <summary>The probe numbers no probe answers to</summary>
    private static readonly string[] MissingProbes = ["G30 K3", Invariant($"G30 K{RemoteProbes.MaxProbes}"), "G30 K-1"];

    /// <summary>G30 with a probe number that names no probe is refused, and nothing moves</summary>
    /// <remarks>
    /// RRF truth: GCodes::SetZProbeNumber, "probe %u not found", after GetLimitedUIValue has refused a
    /// number outside 0 to MaxZProbes − 1. <c>error-missing-parameters-probing-gcodes</c> (G30 K3)
    /// </remarks>
    [TestCaseSource(nameof(MissingProbes))]
    public async Task G30WithAProbeThatDoesNotExistIsRefused(string code)
    {
        await using Bench bench = await StartHomedAsync();
        int before = bench.CanMaster.ScheduledMoves().Count;

        string reply = (await bench.Host.ExecuteCodeAsync(code)).Trim();

        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error"));
            Assert.That(MovesSince(bench, before), Is.Empty);
        });
    }

    /// <summary>G30 on a machine with no probe at all is refused</summary>
    /// <remarks>RRF truth: GCodes::SetZProbeNumber, "probe 0 not found", as M558 has created none</remarks>
    [Test]
    public async Task G30WithNoProbeConfiguredIsRefused()
    {
        await using Bench bench = await StartBenchAsync();
        await bench.Host.ExecuteCodeAsync("G92 X0 Y0 Z0");
        int before = bench.CanMaster.ScheduledMoves().Count;

        string reply = (await bench.Host.ExecuteCodeAsync("G30")).Trim();

        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error"));
            Assert.That(MovesSince(bench, before), Is.Empty);
        });
    }

    // ---- P -----------------------------------------------------------------------------------------

    /// <summary>The probe point numbers outside RepRapFirmware's table</summary>
    private static readonly string[] OutOfRangePoints = ["G30 P-1 X0 Y0 Z-99999", Invariant($"G30 P{MaxProbePoints} X0 Y0 Z-99999")];

    /// <summary>G30 P with a point number outside the table is refused, and nothing moves</summary>
    /// <remarks>RRF truth: ExecuteG30, "probe point index out of range"</remarks>
    [TestCaseSource(nameof(OutOfRangePoints))]
    public async Task G30PWithAPointOutOfRangeIsRefused(string code)
    {
        await using Bench bench = await StartHomedAsync();
        int before = bench.CanMaster.ScheduledMoves().Count;

        string reply = (await bench.Host.ExecuteCodeAsync(code)).Trim();

        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error"));
            Assert.That(MovesSince(bench, before), Is.Empty);
        });
    }

    // ---- Macros --------------------------------------------------------------------------------------

    /// <summary>
    /// A move in retractprobe.g after a failed G30 S-1 starts from where the dive left the head
    /// </summary>
    /// <remarks>
    /// The G30 half of <c>g29-g30-retract-macro-position</c>, which has no reference yet: the retract
    /// macro lifts Z 5 mm relative, which comes out at 2000 steps when the position the macro starts
    /// from is where the dive ended. The case uses retractprobe.g, which DSF's G30 does not fall back
    /// to (<see cref="G30RunsTheUnnumberedProbeMacros"/>), so this uses retractprobe0.g
    /// </remarks>
    [Test]
    public async Task RetractProbeAfterAFailedG30StartsFromWhereTheDiveLeftTheHead()
    {
        const float lift = 5.0f;
        await using Bench bench = await StartHomedAsync(prepareSd: sd => sd.WriteSys("retractprobe0.g", Invariant($"G91\nG1 Z{lift} F600\nG90\n")));
        int before = bench.CanMaster.ScheduledMoves().Count;

        string reply = await ProbeBedAsync(bench, "G30 S-1", NoBed);
        await bench.Host.ExecuteCodeAsync("M400");

        ScheduledMove[] moves = MovesSince(bench, before);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain("not triggered"));
            Assert.That(moves.Select(move => StepsOf(move, ZDriver)), Is.EqualTo(new[] { DiveSteps, (int)(lift * ZStepsPerMm) }),
                        "the dive, then the macro's relative lift measured from where the dive ended");
        });
    }

    // ---- Known gaps ----------------------------------------------------------------------------------

    /// <summary>The axes homed before each attempt at G30 that RepRapFirmware refuses</summary>
    private static readonly string[] NotEnoughHomed = ["", "X0", Invariant($"Z{StartHeight}")];

    /// <summary>G30 refuses to probe until X and Y are homed</summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp case 30 checks <c>AxesToHomeBeforeProbing()</c>, X and Y on a Cartesian
    /// machine (Kinematics.h; all three towers on a delta), against the homed axes before anything
    /// else, replying "Insufficient axes homed for bed probing". DSF does not check
    /// </remarks>
    [TestCaseSource(nameof(NotEnoughHomed))]
    [Category("KnownGap")]
    public async Task G30RefusesUntilXAndYAreHomed(string homed)
    {
        await using Bench bench = await StartBenchAsync(ProbeConfig() + "\n" + MacroCounters, CountingMacros("0"));
        if (homed.Length > 0)
        {
            await bench.Host.ExecuteCodeAsync($"G92 {homed}");
        }
        int before = bench.CanMaster.ScheduledMoves().Count;

        string reply = (await bench.Host.ExecuteCodeAsync("G30", timeoutMs: 60_000)).Trim();

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain("homed").IgnoreCase);
            Assert.That(MovesSince(bench, before), Is.Empty);
            Assert.That(await bench.Host.GlobalAsync("deployRan"), Is.Zero);
        });
    }

    /// <summary>
    /// G30 on an unhomed Z dives from where the head is as a homing move, 1.1 times Z's length
    /// </summary>
    /// <remarks>
    /// RRF truth: probingAtPoint3 targets <c>-1.1 * AxisTotalLength(Z_AXIS)</c> when Z is not homed,
    /// and only <c>AxisMinimum(Z) - dive height + trigger height</c> once it is. DSF moves to its start
    /// height and dives to the homed target whatever
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task G30WithZNotHomedDivesAsAHomingMove()
    {
        const float bed = -2.5f;
        await using Bench bench = await StartBenchAsync(ProbeConfig());
        await bench.Host.ExecuteCodeAsync("G90");
        await bench.Host.ExecuteCodeAsync("G92 X0 Y0");
        int before = bench.CanMaster.ScheduledMoves().Count;

        string reply = await ProbeBedAsync(bench, "G30", bed, startZ: 0.0f);
        await bench.Host.ExecuteCodeAsync("M400");

        ScheduledMove[] moves = MovesSince(bench, before);
        (double user, _) = await SettledZAsync(bench, StartHeight);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Is.Empty);
            Assert.That(moves.Select(move => StepsOf(move, ZDriver)),
                        Is.EqualTo(new[] { (int)(-1.1f * (Machines.ZMax - ZMin) * ZStepsPerMm), LiftSteps }),
                        "the dive from where Z is, the length of a homing move, and the lift");
            Assert.That(user, Is.EqualTo(StartHeight).Within(1e-3));
        });
    }

    /// <summary>
    /// A G30 whose probe never triggers still lifts back to the start height
    /// </summary>
    /// <remarks>
    /// RRF truth: plain G30 goes from probingAtPoint4 to 4a whatever the tap's outcome, and 4a lifts to
    /// <c>GetStartingHeight</c>, measured from Z0 on the unchanged datum. <c>g30-single-probe</c> and
    /// <c>g30-height-correction-and-probe-number</c> record the lift of 40800 steps from Z −101 to Z 1.
    /// DSF returns the error straight after the dive
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task G30LiftsBackToTheStartHeightWhenTheProbeNeverTriggers()
    {
        await using Bench bench = await StartHomedAsync();
        int before = bench.CanMaster.ScheduledMoves().Count;

        string reply = await ProbeBedAsync(bench, "G30", NoBed);
        await bench.Host.ExecuteCodeAsync("M400");

        ScheduledMove[] moves = MovesSince(bench, before);
        (double user, _) = await SettledZAsync(bench, StartHeight);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain("not triggered"));
            Assert.That(moves.Select(move => StepsOf(move, ZDriver)), Is.EqualTo(new[] { DiveSteps, -DiveSteps }), "the dive and the lift");
            Assert.That(user, Is.EqualTo(StartHeight).Within(1e-3));
        });
    }

    /// <summary>
    /// G30 S-1, S-2 and S-3 tap once, whatever M558 A says, and leave the head where the probe stopped
    /// </summary>
    /// <remarks>
    /// RRF truth: probingAtPoint4 sends S-1, S-2 and S-3 straight to probingAtPoint7 after the first
    /// tap ("G30 S-1 command taps once"), skipping CheckIfMoreTapsNeeded and the lift of 4a.
    /// <c>g30-report-stopped-height</c> and <c>g30-set-probe-trigger-height</c> end at the bottom of
    /// their dive. DSF taps up to M558 A times and lifts after each
    /// </remarks>
    [TestCase(-1)]
    [TestCase(-2)]
    [TestCase(-3)]
    [Category("KnownGap")]
    public async Task TheSParametersTapOnceAndLeaveTheHeadWhereItStopped(int s)
    {
        await using Bench bench = await StartHomedAsync("A3 S0.03");
        await bench.Host.ExecuteCodeAsync("M563 P0 S\"probe-tool\"");
        await bench.Host.ExecuteCodeAsync("T0 P0");
        int before = bench.CanMaster.ScheduledMoves().Count;

        string reply = await ProbeBedAsync(bench, $"G30 S{s}", Bed);
        await bench.Host.ExecuteCodeAsync("M400");

        ScheduledMove[] moves = MovesSince(bench, before);
        double machine = await SettledValueAsync(bench.Host, model => model.Move.Axes[2].MachinePosition, Bed);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Not.StartWith("Error"));
            Assert.That(moves.Select(move => StepsOf(move, ZDriver)), Is.EqualTo(new[] { DiveSteps }), "one dive and no lift");
            Assert.That(machine, Is.EqualTo(Bed).Within(1e-3), "the head stays where the probe stopped");
        });
    }

    /// <summary>
    /// G30 S-2 sets the selected tool's Z offset so that the point the probe stopped at reads as the
    /// trigger height, and marks the offset as probed
    /// </summary>
    /// <remarks>
    /// RRF truth: probingAtPoint7 calls <c>currentTool->SetOffset(Z_AXIS, -g30zHeightError, true)</c>,
    /// the height error being the stopped height less the trigger height, which sets bit Z of
    /// <c>offsetsProbed</c> (Tool.cpp), then <c>ToolOffsetInverseTransform</c>. <c>g30-set-tool-z-offset</c>
    /// records <c>tools[0].offsetsProbed</c> going 0 to 4. DSF refuses S-2
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task G30S2SetsTheSelectedToolsZOffset()
    {
        await using Bench bench = await StartHomedAsync();
        await bench.Host.ExecuteCodeAsync("M563 P0 S\"probe-tool\"");
        await bench.Host.ExecuteCodeAsync("G10 P0 Z-0.5");
        await bench.Host.ExecuteCodeAsync("T0 P0");

        string reply = await ProbeBedAsync(bench, "G30 S-2", Bed);
        await bench.Host.ExecuteCodeAsync("M400");

        const float offset = -(Bed - TriggerHeight);
        (float Offset, int Probed, float? User, float? Machine) after = await bench.Host.ReadModelAsync(
            model => (model.Tools[0]!.Offsets[2], model.Tools[0]!.OffsetsProbed, model.Move.Axes[2].UserPosition, model.Move.Axes[2].MachinePosition));
        Assert.Multiple(() =>
        {
            Assert.That(reply, Is.Empty);
            Assert.That(after.Offset, Is.EqualTo(offset).Within(1e-3f), "tools[0].offsets[2] is minus the height error");
            Assert.That(after.Probed, Is.EqualTo(1 << 2), "tools[0].offsetsProbed has Z's bit");
            Assert.That(after.User - after.Machine, Is.EqualTo(offset).Within(1e-3f), "the user position takes the new offset");
        });
    }

    /// <summary>A G30 S-2 whose probe never triggers fails and leaves the tool's Z offset alone</summary>
    /// <remarks>
    /// RepRapFirmware's probingAtPoint7 still calls <c>SetOffset(Z_AXIS, -g30zHeightError, true)</c> after the
    /// error, writing -0 and marking the offset probed (<c>g30-set-tool-z-offset</c> records
    /// <c>offsetsProbed</c> going 0 to 4). That is a bug: a failed probe changing nothing is what plain G30,
    /// G30 S-3 and G30 P do, and is what this pins (rrf-differences.md, Error handling)
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task G30S2WithTheProbeNeverTriggeringLeavesTheToolOffset()
    {
        const float toolZOffset = -0.5f;
        await using Bench bench = await StartHomedAsync();
        await bench.Host.ExecuteCodeAsync("M563 P0 S\"probe-tool\"");
        await bench.Host.ExecuteCodeAsync(Invariant($"G10 P0 Z{toolZOffset}"));
        await bench.Host.ExecuteCodeAsync("T0 P0");
        int before = bench.CanMaster.ScheduledMoves().Count;

        string reply = await ProbeBedAsync(bench, "G30 S-2", NoBed);

        (float Offset, int Probed) after = await bench.Host.ReadModelAsync(model => (model.Tools[0]!.Offsets[2], model.Tools[0]!.OffsetsProbed));
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain("not triggered"));
            Assert.That(Dives(bench, before), Has.Length.EqualTo(1), "the probe dived once");
            Assert.That(after.Offset, Is.EqualTo(toolZOffset).Within(1e-4f), "tools[0].offsets[2] is unchanged");
            Assert.That(after.Probed, Is.Zero, "tools[0].offsetsProbed is unchanged");
        });
    }

    /// <summary>A board that refuses to watch the probe is not then told to stop watching it</summary>
    /// <remarks>
    /// RRF truth: probingAtPoint3 abandons the code when <c>SetProbing(true)</c> fails and never calls
    /// <c>SetProbing(false)</c>, so the board gets the one ChangeInputMonitor. DSF sends the idle rate
    /// from its finally block however the tap ended
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task ABoardThatRefusesToWatchTheProbeIsToldNothingMore()
    {
        await using Bench bench = await StartHomedAsync();
        bench.CanMaster.ReportWith<CanMessageChangeInputMonitorV1>("refused", CodeResult.Error);
        int monitorsBefore = bench.CanMaster.CanMessages<CanMessageChangeInputMonitorV1>().Count;

        await bench.Host.ExecuteCodeAsync("G30");

        Assert.That(MonitorChanges(bench, monitorsBefore).Select(change => change.Message.Param),
                    Is.EqualTo(new[] { ProbeArming.ActiveReportInterval }));
    }

    /// <summary>G30 falls back to deployprobe.g and retractprobe.g when the probe has no macros of its own</summary>
    /// <remarks>
    /// RRF truth: <c>DeployZProbe</c> and <c>RetractZProbe</c> (GCodes3.cpp) run <c>deployprobe.g</c> and
    /// <c>retractprobe.g</c>, with the probe number in K, when <c>deployprobe&lt;K&gt;.g</c> and
    /// <c>retractprobe&lt;K&gt;.g</c> do not exist. DSF's M401 and M402 fall back; its G30 does not
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task G30RunsTheUnnumberedProbeMacros()
    {
        await using Bench bench = await StartHomedAsync(configExtra: MacroCounters, prepareSd: CountingMacros(""));

        string reply = await ProbeBedAsync(bench, "G30", Bed);

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Is.Empty);
            Assert.That(await bench.Host.GlobalAsync("deployRan"), Is.EqualTo(1));
            Assert.That(await bench.Host.GlobalAsync("retractRan"), Is.EqualTo(1));
        });
    }

    /// <summary>G30 leaves a probe deployed by M401 deployed</summary>
    /// <remarks>
    /// RRF truth: <c>DeployZProbe</c> and <c>RetractZProbe</c> do nothing while the probe is
    /// <c>deployedByUser</c>, which M401 sets, so the probe stays down until M402. DSF runs both macros
    /// regardless
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task G30LeavesAProbeDeployedByM401Alone()
    {
        await using Bench bench = await StartHomedAsync(configExtra: MacroCounters, prepareSd: CountingMacros("0"));
        await bench.Host.ExecuteCodeAsync("M401");

        string reply = await ProbeBedAsync(bench, "G30", Bed);

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Is.Empty);
            Assert.That(await bench.Host.GlobalAsync("deployRan"), Is.EqualTo(1), "only M401 deployed it");
            Assert.That(await bench.Host.GlobalAsync("retractRan"), Is.Zero, "and G30 did not retract it");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.DeployedByUser), Is.True);
        });
    }

    /// <summary>
    /// The deploy and retract macros around two taps: a BLTouch is deployed before and retracted after
    /// each, any other probe deployed once before the first and retracted once after the last
    /// </summary>
    /// <remarks>
    /// RRF truth: ExecuteG30 deploys only a probe that is not a BLTouch; probingAtPoint2a deploys a
    /// BLTouch before every tap and probingAtPoint4 retracts it after every tap, and probingAtPoint5
    /// retracts any other probe once the reading is accepted. Each deploy appends 1 to the log and each
    /// retract 2. DSF deploys every probe before every tap after the first as well, and retracts once
    /// </remarks>
    [TestCase(8, 12)]
    [TestCase(9, 1212)]
    [Category("KnownGap")]
    public async Task TheProbeIsDeployedAndRetractedAroundTheTapsAsItsTypeNeeds(int type, int expected)
    {
        string probe = Invariant($"M558 K0 P{type} C\"{Expansion.GpioIn(Machines.DriverBoard, 1)}\" H{DiveHeight} F{ProbeSpeed} T{TravelSpeed} A2 S0.03\nG31 K0 Z{TriggerHeight} P500");
        await using Bench bench = await StartBenchAsync(probe + "\nglobal macroLog = 0", sd =>
        {
            sd.WriteSys("deployprobe0.g", "set global.macroLog = global.macroLog * 10 + 1\n");
            sd.WriteSys("retractprobe0.g", "set global.macroLog = global.macroLog * 10 + 2\n");
        });
        await bench.Host.ExecuteCodeAsync("G90");
        await bench.Host.ExecuteCodeAsync(Invariant($"G92 X0 Y0 Z{StartHeight}"));

        string reply = await ProbeBedAsync(bench, "G30", Bed);

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Is.Empty);
            Assert.That(await bench.Host.GlobalAsync("macroLog"), Is.EqualTo(expected));
        });
    }

    /// <summary>
    /// Two taps that agree: the first resets Z's datum, the second measures from it, and the head lifts
    /// to the start height after each
    /// </summary>
    /// <remarks>
    /// RRF truth: probingAtPoint4 resets Z to the trigger height after the first tap (<c>tapsDone &lt;= 1</c>),
    /// so the second tap stops at the trigger height and its height error is zero; probingAtPoint4a lifts
    /// to <c>GetStartingHeight</c> after every tap, and the second dive starts from there. DSF moves to its
    /// start height before the second dive, measures both taps on the old datum and does not lift after
    /// the tap that agrees
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task G30TapsFromTheLiftAndMeasuresTheSecondTapFromTheNewDatum()
    {
        await using Bench bench = await StartHomedAsync("A3 S0.03");
        int before = bench.CanMaster.ScheduledMoves().Count;

        string reply = await ProbeBedAsync(bench, "G30", Bed);
        await bench.Host.ExecuteCodeAsync("M400");

        ScheduledMove[] moves = MovesSince(bench, before);
        (double user, _) = await SettledZAsync(bench, StartHeight);
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Is.Empty);
            Assert.That(moves.Select(move => StepsOf(move, ZDriver)), Is.EqualTo(new[] { DiveSteps, LiftSteps, DiveSteps, LiftSteps }));
            Assert.That(moves.Select(move => move.Has(ScheduleMoveFlags.CheckEndstops)), Is.EqualTo(new[] { true, false, true, false }));
            Assert.That(user, Is.EqualTo(StartHeight).Within(1e-3));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.LastStopHeight), Is.EqualTo(TriggerHeight).Within(1e-3f),
                        "the second tap stopped at the trigger height of the datum the first one set");
        });
    }

    /// <summary>
    /// Taps that never agree: G30 uses the mean of all of them and says the readings were not
    /// consistent
    /// </summary>
    /// <remarks>
    /// RRF truth: CheckIfMoreTapsNeeded, once M558 A taps have been made, takes
    /// <c>g30zHeightErrorSum / tapsDone</c> and sets "Z probe readings not consistent" as the code's
    /// error (its comment says it issues a warning; the message type is not asserted). The first tap's
    /// error is zero, since it set the datum, so the mean is of both readings: 4.0 and 4.5 here, so Z
    /// reads 0.25 below where the second tap's lift took it. DSF says nothing and lifts from the last tap
    /// by its second dive height
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task G30WithReadingsThatNeverAgreeUsesTheirMeanAndSaysSo()
    {
        const float secondBed = Bed + 0.5f;
        await using Bench bench = await StartHomedAsync("A2 S0.01");
        await WaitForClockSyncAsync(bench);
        int before = bench.CanMaster.ScheduledMoves().Count;

        string reply = await ProbeBedAsync(bench, bench.Host.ExecuteCodeAsync("G30", timeoutMs: 60_000), before, StartHeight,
                                           tap => tap == 0 ? Bed : secondBed);
        await bench.Host.ExecuteCodeAsync("M400");

        ScheduledMove[] moves = MovesSince(bench, before);
        const float secondStop = secondBed - Bed + TriggerHeight, meanError = (secondBed - Bed) / 2;
        (double user, _) = await SettledZAsync(bench, StartHeight - meanError);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Contain("Z probe readings not consistent"));
            Assert.That(moves.Select(move => StepsOf(move, ZDriver)),
                        Is.EqualTo(new[] { DiveSteps, LiftSteps, DiveSteps, (int)MathF.Round((StartHeight - secondStop) * ZStepsPerMm) }),
                        "each tap lifts to the start height of the datum the first one set");
            Assert.That(user, Is.EqualTo(StartHeight - meanError).Within(1e-3), "then Z is corrected by the mean height error");
        });
    }

    /// <summary>
    /// With a second probing speed slower than the first, G30 taps once fast, then at the slow speed,
    /// and only the slow taps count
    /// </summary>
    /// <remarks>
    /// RRF truth: <c>FastThenSlowProbing()</c> starts <c>tapsDone</c> at −1 (InitialiseTaps), so with
    /// M558 A1 there is a fast tap at the first speed (<c>GetProbingSpeed(-1)</c>) whose reading is not
    /// accumulated, a lift, and the one counted tap at the second speed. DSF counts the fast tap as the
    /// only one
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task FastThenSlowProbingTapsFastFirstAndCountsTheSlowTap()
    {
        const int fast = 600, slow = 120;
        string probe = Invariant($"{ProbeLine} H{DiveHeight} F{fast}:{slow} T{TravelSpeed} A1\nG31 K0 Z{TriggerHeight} P500");
        await using Bench bench = await StartBenchAsync(probe);
        await bench.Host.ExecuteCodeAsync("G90");
        await bench.Host.ExecuteCodeAsync(Invariant($"G92 X0 Y0 Z{StartHeight}"));
        int before = bench.CanMaster.ScheduledMoves().Count;

        string reply = await ProbeBedAsync(bench, "G30", Bed);

        ScheduledMove[] dives = Dives(bench, before);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Is.Empty);
            Assert.That(dives.Select(dive => MoveCapture.MmPerSecond(dive.Header.TopSpeed)),
                        Is.EqualTo(new[] { fast / 60.0f, slow / 60.0f }).Within(0.01f), "a fast tap, then a slow one");
        });
    }

    /// <summary>
    /// G30 P probes at a point given as the probe's position, records the height error, and G30 S-1 on
    /// the last point reports them all
    /// </summary>
    /// <remarks>
    /// RRF truth: ExecuteG30 stores the X and Y given as the probe point (SetXYBedProbePoint);
    /// probingAtPoint0 lifts to the start height, probingAtPoint1 travels so that the probe is over the
    /// point (<c>GetProbeCoordinates(..., true)</c> subtracts the probe's offsets), probingAtPoint4a lifts
    /// back to the start height and probingAtPoint5 records the height error (SetZBedProbePoint).
    /// <c>FinishedBedProbing</c> with S-1 replies "G32 bed probe heights:" with each error, the mean and
    /// the deviation from it, and Z's datum is untouched. <c>g30-probe-point</c> records the travel and the
    /// dive. DSF refuses G30 P
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task G30PProbesAtThePointAndS1ReportsTheHeights()
    {
        const float offsetX = 2.0f, offsetY = -3.0f, secondBed = Bed + 0.2f;
        await using Bench bench = await StartBenchAsync(Invariant($"{ProbeLine} H{DiveHeight} F{ProbeSpeed} T{TravelSpeed}\nG31 K0 Z{TriggerHeight} P500 X{offsetX} Y{offsetY}"));
        await bench.Host.ExecuteCodeAsync("G90");
        await bench.Host.ExecuteCodeAsync(Invariant($"G92 X0 Y0 Z{StartHeight}"));
        int before = bench.CanMaster.ScheduledMoves().Count;

        string first = await ProbeBedAsync(bench, "G30 P0 X5 Y5 Z-99999", Bed);
        string second = await ProbeBedAsync(bench, "G30 P1 X10 Y10 Z-99999 S-1", secondBed);
        await bench.Host.ExecuteCodeAsync("M400");

        ScheduledMove[] moves = MovesSince(bench, before);
        const float error0 = Bed - TriggerHeight, error1 = secondBed - TriggerHeight;
        Assert.Multiple(async () =>
        {
            Assert.That(first, Is.Empty);
            Assert.That(second, Is.EqualTo(Invariant($"G32 bed probe heights: {error0:F3} {error1:F3}, mean {(error0 + error1) / 2:F3}, deviation from mean {(error1 - error0) / 2:F3}")));
            Assert.That(moves.Select(move => (StepsOf(move, Machines.XDriver), StepsOf(move, Machines.YDriver), StepsOf(move, ZDriver))),
                        Is.EqualTo(new[]
                        {
                            ((int)((5 - offsetX) * Machines.XyStepsPerMm), (int)((5 - offsetY) * Machines.XyStepsPerMm), 0),
                            (0, 0, DiveSteps),
                            (0, 0, (int)MathF.Round((StartHeight - Bed) * ZStepsPerMm)),
                            ((int)(5 * Machines.XyStepsPerMm), (int)(5 * Machines.XyStepsPerMm), 0),
                            (0, 0, DiveSteps),
                            (0, 0, (int)MathF.Round((StartHeight - secondBed) * ZStepsPerMm)),
                        }), "travel with the probe over the point, dive, lift to the start height, for each point");
            Assert.That(moves.Select(move => move.Has(ScheduleMoveFlags.CheckEndstops)), Is.EqualTo(new[] { false, true, false, false, true, false }));
            Assert.That(await bench.Host.ReadAxisAsync('Z', axis => axis.UserPosition), Is.EqualTo(StartHeight).Within(1e-3f), "Z's datum is untouched");
        });
    }

    /// <summary>
    /// G30 P with a real Z records that height for the point without probing, and S-1 reports it
    /// </summary>
    /// <remarks>
    /// RRF truth: ExecuteG30 takes a Z above SILLY_Z_VALUE (−9999) as the height error itself
    /// (SetZBedProbePoint) and, with S −1 or more, calls FinishedBedProbing at once.
    /// RandomProbePointSet::ReportProbeHeights formats the report; FinishedBedProbing then clears the
    /// heights, so the next report holds only what was set after it. DSF refuses G30 P
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task G30PWithAHeightRecordsItWithoutProbing()
    {
        await using Bench bench = await StartHomedAsync();
        int before = bench.CanMaster.ScheduledMoves().Count;

        string first = (await bench.Host.ExecuteCodeAsync("G30 P0 X10 Y20 Z0.3")).Trim();
        string report = (await bench.Host.ExecuteCodeAsync("G30 P1 X30 Y40 Z-0.2 S-1")).Trim();
        string afterClearing = (await bench.Host.ExecuteCodeAsync("G30 P0 X10 Y20 Z0.1 S-1")).Trim();

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Empty);
            Assert.That(report, Is.EqualTo("G32 bed probe heights: 0.300 -0.200, mean 0.050, deviation from mean 0.250"));
            Assert.That(afterClearing, Is.EqualTo("G32 bed probe heights: 0.100, mean 0.100, deviation from mean 0.000"),
                        "the report cleared the heights it reported");
            Assert.That(MovesSince(bench, before), Is.Empty, "nothing is probed");
        });
    }

    /// <summary>
    /// G30 P with S above zero asks for a calibration: too few points, or a Cartesian machine with no
    /// M671 leadscrews, is an error
    /// </summary>
    /// <remarks>
    /// RRF truth: Move::FinishedBedProbing replies "Bed calibration : %d factor calibration requested but
    /// only %d points provided" when S exceeds the points, and "This kinematics does not support
    /// auto-calibration" when ZLeadscrewKinematics has fewer than two leadscrews. DSF refuses G30 P
    /// </remarks>
    [TestCase(3, "only 2 points")]
    [TestCase(2, "does not support auto-calibration")]
    [Category("KnownGap")]
    public async Task G30PWithACalibrationTheMachineCannotDoIsAnError(int s, string reason)
    {
        await using Bench bench = await StartHomedAsync();

        string first = (await bench.Host.ExecuteCodeAsync("G30 P0 X10 Y20 Z0.3")).Trim();
        string calibrate = (await bench.Host.ExecuteCodeAsync($"G30 P1 X30 Y40 Z-0.2 S{s}")).Trim();

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Empty);
            Assert.That(calibrate, Does.StartWith("Error").And.Contain(reason));
        });
    }

    /// <summary>
    /// G30 with a manual probe (M558 P0) asks the user to bring the nozzle down to the bed, and takes
    /// the height they confirm as where the probe stopped
    /// </summary>
    /// <remarks>
    /// RRF truth: probingAtPoint3 calls <c>DoManualBedProbe</c> for a probe of type none, which opens a
    /// "Manual bed probing" message box with Z jog controls and waits; on M292, probingAtPoint4 takes the
    /// current Z as the stopped height, and plain G30 then sets Z to the trigger height and lifts as
    /// after any tap. DSF refuses a probe that cannot stop a move
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task G30WithAManualProbeAsksTheUserForTheHeight()
    {
        await using Bench bench = await StartBenchAsync(Invariant($"M558 K0 P0 H{DiveHeight} F{ProbeSpeed} T{TravelSpeed}\nG31 K0 Z{TriggerHeight}"));
        await bench.Host.ExecuteCodeAsync("G90");
        await bench.Host.ExecuteCodeAsync(Invariant($"G92 X0 Y0 Z{StartHeight}"));

        Task<string> code = bench.Host.ExecuteCodeAsync("G30", timeoutMs: 60_000);
        MessageBox? box = await bench.Host.WaitForModelAsync(model => model.State.MessageBox, messageBox => messageBox is not null || code.IsCompleted,
                                                             "a message box asking for the height", 10_000);
        Assert.That(box, Is.Not.Null, $"G30 asks rather than replying \"{(code.IsCompleted ? (await code).Trim() : string.Empty)}\"");
        Assert.That(box!.Title, Is.EqualTo("Manual bed probing"));

        await bench.Host.ExecuteCodeAsync("M292", DuetAPI.CodeChannel.SBC);
        string reply = (await code).Trim();
        (double user, _) = await SettledZAsync(bench, StartHeight);
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Is.Empty);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.LastStopHeight), Is.EqualTo(StartHeight).Within(1e-3f),
                        "the confirmed height is where the probe stopped");
            Assert.That(user, Is.EqualTo(StartHeight).Within(1e-3), "Z set to the trigger height there, and lifted by the dive height");
        });
    }
}
