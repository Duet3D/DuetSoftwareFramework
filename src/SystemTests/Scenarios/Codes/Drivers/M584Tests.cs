using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Drivers;

/// <summary>M584, mapping drivers to axes and extruders, which also creates and hides axes</summary>
[TestFixture]
[GCode("M584")]
public class M584Tests : BenchFixture
{
    /// <summary>Drivers of <see cref="Machines.DriverBoard"/> that no machine maps, for the U axis the machine scenarios create</summary>
    private const byte UDriver = 4, SecondUDriver = 5;

    /// <summary>A board that answers like the rig's boards but is not part of the rig, so that the order of the boards it is sent to shows</summary>
    private const byte OtherBoard = 3;

    /// <summary>A board address nothing answers at, as in <c>m584-expansion-timeout.yaml</c></summary>
    private const byte AbsentBoard = 99;

    /// <summary>A board that announces itself with <see cref="AnnouncedBoardDrivers"/> drivers, so that one past the end is known not to exist</summary>
    private const byte AnnouncedBoard = 5;

    /// <summary>How many drivers <see cref="AnnouncedBoard"/> announces</summary>
    private const int AnnouncedBoardDrivers = 3;

    /// <summary>Microstepping as it goes over CAN: x16, with bit 15 set for interpolation, which is what every drive starts with</summary>
    private const ushort X16Interpolated = 0x8000 | 16;

    /// <summary>M584s that create U and then fail on P, out of range or with no value</summary>
    private static readonly string[] CreatingUThenFailing = [$"M584 U{Expansion.Driver(Machines.DriverBoard, UDriver)} P9", $"M584 U{Expansion.Driver(Machines.DriverBoard, UDriver)} P"];

    /// <summary>Every CAN message the machine has sent since the capture was last cleared, as the board and the type</summary>
    private static (byte Board, CanMessageType Type)[] SentCanMessages(Bench bench)
        => [.. bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage)
                              .Select(packet => packet.DecodeCanMessage().Header)
                              .Select(header => (header.DstAddress, (CanMessageType)header.MsgType))];

    /// <summary>Steps per mm and microstepping of every driver a steps per mm request carries, in the order the board reads them</summary>
    private static (float StepsPerMm, ushort Microstepping)[] Values(CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping message)
        => [.. Enumerable.Range(0, System.Numerics.BitOperations.PopCount(message.DriversToUpdate))
                         .Select(i => (message.Values[i].GetStepsPerUnit(), message.Values[i].GetMicrostepping()))];

    /// <summary>
    /// M584 maps axes onto other drivers and sends each new driver its axis' steps per mm and
    /// microstepping, in one request per board
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m584-map-axes-to-expansion.yaml</c>: one <c>setStepsPerMmAndMicrostepping</c>
    /// to the 3HC for all three drivers and nothing else. RRF GCodes3.cpp DoDriveMapping collects the
    /// drives it touches and sends them once at the end (Move::UpdateRemoteStepsPerMmAndMicrostepping).
    /// A drive that is not phase stepping is not sent a step mode reset: RRF 3.7.0 fixed sending one to
    /// every remapped driver (commit 97d45a32c, "caused CAN timeouts on boards without M970 support")
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M584MapsAxesAndSendsTheirSettingsToTheNewDrivers()
    {
        const float xStepsPerMm = 123.0f;
        await using Bench bench = await Bench.StartAsync(DriversRig.Config, $"M92 X{xStepsPerMm}");
        float yStepsPerMm = await bench.Host.ReadModelAsync(model => model.Move.Axes[1].StepsPerMm);
        float zStepsPerMm = await bench.Host.ReadModelAsync(model => model.Move.Axes[2].StepsPerMm);

        Assert.That((await bench.Host.ExecuteCodeAsync($"M584 X{DriversRig.MainDriver(3)} Y{DriversRig.MainDriver(4)} Z{DriversRig.MainDriver(5)}")).Trim(), Is.Empty);

        Assert.That(SentCanMessages(bench), Is.EqualTo(new[] { (DriversRig.MainDriverBoard, CanMessageType.SetStepsPerMmAndMicrostepping) }),
                    "one request for the board the drivers are on, and no step mode reset because no drive was phase stepping");
        (byte board, CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping sent) =
            bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>();
        Assert.Multiple(async () =>
        {
            Assert.That(sent.DriversToUpdate, Is.EqualTo(0b111000), "drivers 3, 4 and 5");
            Assert.That(bench.CanMaster.CanPayloads<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>()[0].Payload.Length,
                        Is.EqualTo(new CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping { DriversToUpdate = 0b111000 }.GetActualDataLength()),
                        "carrying a value for those three drivers and no more");
            Assert.That(Values(sent), Is.EqualTo(new[] { (xStepsPerMm, X16Interpolated), (yStepsPerMm, X16Interpolated), (zStepsPerMm, X16Interpolated) }),
                        "each driver gets the settings of the axis it now drives");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes.Select(axis => axis.Drivers.Single()).ToArray()),
                        Is.EqualTo(new[] { new DriverId(DriversRig.MainDriverBoard, 3), new DriverId(DriversRig.MainDriverBoard, 4), new DriverId(DriversRig.MainDriverBoard, 5) }));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].StepsPerMm), Is.EqualTo(xStepsPerMm),
                        "and the axis keeps its settings");
        });
    }

    /// <summary>
    /// M584 E maps the extruders onto drivers, and the list is the whole set: it adds the extruders it
    /// names beyond the ones there are and drops the ones it no longer names
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m584-map-extruder.yaml</c>: the extruder moves to the 1HCL with one
    /// <c>setStepsPerMmAndMicrostepping</c> carrying its 420 steps per mm. RRF GCodes3.cpp DoDriveMapping
    /// sets numExtruders to the length of the list
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M584MapsTheExtrudersAndTheirCountFollowsTheList()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        float eStepsPerMm = await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].StepsPerMm);

        Assert.That((await bench.Host.ExecuteCodeAsync($"M584 E{DriversRig.MainDriver(3)}")).Trim(), Is.Empty);
        Assert.That(SentCanMessages(bench), Is.EqualTo(new[] { (DriversRig.MainDriverBoard, CanMessageType.SetStepsPerMmAndMicrostepping) }));
        CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping sent = bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>().Message;
        Assert.Multiple(async () =>
        {
            Assert.That(sent.DriversToUpdate, Is.EqualTo(0b1000));
            Assert.That(Values(sent), Is.EqualTo(new[] { (eStepsPerMm, X16Interpolated) }));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders.Select(extruder => extruder.Driver).ToArray()),
                        Is.EqualTo(new[] { new DriverId(DriversRig.MainDriverBoard, 3) }));
        });

        bench.CanMaster.ClearCapture();
        Assert.That((await bench.Host.ExecuteCodeAsync($"M584 E{DriversRig.MainDriver(3)}:{DriversRig.MainDriver(4)}")).Trim(), Is.Empty);
        sent = bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>().Message;
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders.Select(extruder => extruder.Driver).ToArray()),
                        Is.EqualTo(new[] { new DriverId(DriversRig.MainDriverBoard, 3), new DriverId(DriversRig.MainDriverBoard, 4) }),
                        "a second driver adds a second extruder");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[1].StepsPerMm), Is.EqualTo(Extruder.DefaultStepsPerMm),
                        "which starts with RRF's default extruder steps per mm (DefaultEDriveStepsPerUnit)");
            Assert.That(sent.DriversToUpdate, Is.EqualTo(0b11000), "and both extruders' drivers are told their settings");
            Assert.That(Values(sent), Is.EqualTo(new[] { (eStepsPerMm, X16Interpolated), (Extruder.DefaultStepsPerMm, X16Interpolated) }));
        });

        Assert.That((await bench.Host.ExecuteCodeAsync($"M584 E{DriversRig.MainDriver(4)}")).Trim(), Is.Empty);
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders.Select(extruder => extruder.Driver).ToArray()),
                    Is.EqualTo(new[] { new DriverId(DriversRig.MainDriverBoard, 4) }),
                    "and a shorter list drops the extruders it no longer names");
    }

    /// <summary>
    /// A phase stepping drive that M584 maps is put back into step/direction mode on the drivers it had,
    /// before it is given the new ones
    /// </summary>
    /// <remarks>
    /// RRF GCodes3.cpp DoDriveMapping: <c>if (move.GetStepMode(drive) == StepMode::phase)
    /// move.SetStepMode(drive, StepMode::stepDir, reply)</c> before <c>SetAxisDriversConfig</c>, and the
    /// same for each extruder the E list names. Otherwise the old drivers would stay phase stepping
    /// with nothing driving them, and the new ones would be reset for no reason (RRF commit 97d45a32c).
    /// Y is remapped as well but was not phase stepping, so it is sent no reset
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M584ResetsAPhaseSteppingDriveOnItsOldDrivers()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync("M970 X1 E1")).Trim(), Is.Empty);
        bench.CanMaster.ClearCapture();

        Assert.That((await bench.Host.ExecuteCodeAsync($"M584 X{DriversRig.MainDriver(3)} Y{DriversRig.MainDriver(4)} E{DriversRig.MainDriver(5)}")).Trim(), Is.Empty);

        Assert.That(SentCanMessages(bench), Is.EqualTo(new[]
                    {
                        (DriversRig.MainDriverBoard, CanMessageType.M970),
                        (DriversRig.ClosedLoopBoard, CanMessageType.M970),
                        (DriversRig.MainDriverBoard, CanMessageType.SetStepsPerMmAndMicrostepping),
                    }),
                    "the resets come before the new drivers are told their settings");
        Assert.That(bench.CanMaster.CanMessages<CanMessageM970>().Select(message => (message.Board, message.Message.P, message.Message.S)),
                    Is.EqualTo(new[]
                    {
                        (DriversRig.MainDriverBoard, (byte?)0, (byte?)0),
                        (DriversRig.ClosedLoopBoard, (byte?)0, (byte?)0),
                    }),
                    "X's old driver and the extruder's old driver go back to step/direction");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Drivers[0]),
                        Is.EqualTo(new DriverId(DriversRig.MainDriverBoard, 3)));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].PhaseStep), Is.False,
                        "and the drive is no longer phase stepping");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].PhaseStep), Is.False);
        });
    }

    /// <summary>
    /// A board that never answers the step-mode reset does not stop M584, but is reported
    /// </summary>
    /// <remarks>
    /// RepRapFirmware discards what <c>Move::SetStepMode</c> returns (GCodes3.cpp DoDriveMapping) and
    /// carries on, while the reply buffer keeps the text. The mapping is done and the new drivers are
    /// told their settings; the drive is still marked as phase stepping, because RRF clears
    /// <c>remotePhaseStepDrives</c> only once every driver took the reset. DSF reports the timeout as an
    /// error where RRF reports it at OK severity (rrf-differences.md section 6.1)
    /// </remarks>
    [Test]
    public async Task M584ReportsAStepModeResetThatWentUnanswered()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        Assert.That((await bench.Host.ExecuteCodeAsync("M970 X1 Y1")).Trim(), Is.Empty);
        bench.CanMaster.NeverAnswer<CanMessageM970>();
        bench.CanMaster.ClearCapture();

        string reply = await bench.Host.ExecuteCodeAsync($"M584 X{DriversRig.MainDriver(3)} Y{DriversRig.MainDriver(4)}");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error"));
            Assert.That(reply.TrimEnd().Split('\n'), Has.Length.EqualTo(2),
                        "one timeout line per drive, because a drive stops at the first driver that did not answer");
            Assert.That(reply, Does.Contain($"CAN response timeout: board {DriversRig.MainDriverBoard}, req type {(int)CanMessageType.M970}"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Drivers[0]),
                        Is.EqualTo(new DriverId(DriversRig.MainDriverBoard, 3)), "the mapping stands");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[1].Drivers[0]),
                        Is.EqualTo(new DriverId(DriversRig.MainDriverBoard, 4)));
            Assert.That(bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>().Message.DriversToUpdate,
                        Is.EqualTo(0b11000), "and the new drivers are told their settings");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].PhaseStep), Is.True,
                        "and the drive is still phase stepping, since the board did not say it stopped");
        });
    }

    /// <summary>
    /// A bare M584 reports the mapping in board.driver notation, with the extruders as one group
    /// after the axes
    /// </summary>
    /// <remarks><c>testcases/drivers/m584-report-mapping.yaml</c></remarks>
    [Test]
    public async Task M584ReportsTheMapping()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync("M584")).TrimEnd(),
                    Is.EqualTo($"Driver assignments: X{DriversRig.MainDriver(0)} Y{DriversRig.MainDriver(1)} Z{DriversRig.MainDriver(2)} E{DriversRig.ClosedLoopDriver(0)}, 3 axes visible"));
    }

    /// <summary>
    /// A machine whose axes have no drivers says "none", even when it has an extruder
    /// </summary>
    /// <remarks>
    /// RRF GCodes3.cpp DoDriveMapping report branch: <c>printed</c> is set by the axis drivers alone,
    /// so the extruders are listed and " none" follows them
    /// </remarks>
    [Test]
    public async Task M584ReportsNoneWhenNoAxisHasADriver()
    {
        await using Bench bench = await Bench.StartAsync($"M584 E{DriversRig.ClosedLoopDriver(0)}");

        Assert.That((await bench.Host.ExecuteCodeAsync("M584")).TrimEnd(),
                    Is.EqualTo($"Driver assignments: E{DriversRig.ClosedLoopDriver(0)} none, 0 axes visible"));
    }

    /// <summary>
    /// M584 P hides the axes past the count it is given, without taking their drivers away, and the
    /// restore points hold only the visible axes
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m584-hide-axis.yaml</c>: P2 takes the Z coordinate out of every restore
    /// point. RRF RestorePoint.cpp sizes <c>coords</c> by <c>GetVisibleAxes()</c>
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M584PHidesAxes()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync("M584 P2")).Trim(), Is.Empty);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes.Select(axis => axis.Visible).ToArray()),
                        Is.EqualTo(new[] { true, true, false }), "P2 leaves X and Y visible and hides Z");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[2].Drivers.Count), Is.EqualTo(1),
                        "a hidden axis keeps its drivers");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.MotionSystems[0].RestorePoints.Select(point => point.Coords.Count).ToArray()),
                        Is.EqualTo(Enumerable.Repeat(2, DuetControlServer.Motion.RestorePoint.NumVisible)),
                        "each restore point holds the two visible axes");
            Assert.That(SentCanMessages(bench), Is.Empty, "hiding an axis is nothing the boards need to know");
        });

        Assert.That((await bench.Host.ExecuteCodeAsync("M584")).TrimEnd(),
                    Is.EqualTo($"Driver assignments: X{DriversRig.MainDriver(0)} Y{DriversRig.MainDriver(1)} Z{DriversRig.MainDriver(2)} E{DriversRig.ClosedLoopDriver(0)}, 2 axes visible"));

        Assert.That((await bench.Host.ExecuteCodeAsync("M584 P3")).Trim(), Is.Empty);
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.MotionSystems[0].RestorePoints.Select(point => point.Coords.Count).ToArray()),
                    Is.EqualTo(Enumerable.Repeat(3, DuetControlServer.Motion.RestorePoint.NumVisible)),
                    "and showing Z again puts it back");
    }

    /// <summary>
    /// P counts axes, and fewer than two visible or more than there are is refused without changing which are visible
    /// </summary>
    /// <remarks>RRF GCodes3.cpp DoDriveMapping: <c>nva &gt;= MinVisibleAxes &amp;&amp; nva &lt;= numTotalAxes</c>, with MinVisibleAxes 2 (Configuration.h), else "Invalid number of visible axes"</remarks>
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(4)]
    [Category("KnownGap")]
    public async Task M584RefusesAVisibleAxisCountOutOfRange(int visible)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config, "M584 P2");

        string reply = await bench.Host.ExecuteCodeAsync($"M584 P{visible}");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain("Invalid number of visible axes"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes.Select(axis => axis.Visible).ToArray()),
                        Is.EqualTo(new[] { true, true, false }), "the axes that were visible still are");
        });
    }

    /// <summary>
    /// An M584 whose P is refused still applies its mapping, and tells the boards about it
    /// </summary>
    /// <remarks>
    /// RRF GCodes3.cpp DoDriveMapping reads P after the axis letters and only records the error, then
    /// sends the steps per mm of every drive it mapped (<c>rslt = max(rslt,
    /// UpdateRemoteStepsPerMmAndMicrostepping(...))</c>). The model and the boards have to agree on
    /// which drivers carry the axes, whatever happened to P
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M584WithARefusedPStillSendsTheMappingToTheBoards()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync($"M584 X{DriversRig.MainDriver(3)} U{DriversRig.MainDriver(4)} P9");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain("Invalid number of visible axes"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes.Select(axis => (axis.Letter, axis.Drivers.Single())).ToArray()),
                        Is.EqualTo(new[]
                        {
                            ('X', new DriverId(DriversRig.MainDriverBoard, 3)),
                            ('Y', new DriverId(DriversRig.MainDriverBoard, 1)),
                            ('Z', new DriverId(DriversRig.MainDriverBoard, 2)),
                            ('U', new DriverId(DriversRig.MainDriverBoard, 4)),
                        }));
            Assert.That(bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>().Select(message => (message.Board, message.Message.DriversToUpdate)),
                        Is.EqualTo(new[] { (DriversRig.MainDriverBoard, (ushort)0b11000) }),
                        "and the drivers of X and U were told their settings");
        });
    }

    /// <summary>
    /// M584 creates an axis the first time a letter is named, with the defaults a drive starts life
    /// with, and every per-axis array of the object model grows with it
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m584-create-additional-axis.yaml</c>: the new axis has 80 steps per mm,
    /// x16 interpolated microstepping, 6000 mm/min, 1000 mm/s², 900 mm/min jerk, travel 0 to 200 and no
    /// current, which are what Move::Init gives every drive. Its driver is sent the steps per mm and
    /// microstepping and nothing else. <c>sensors.endstops</c> gains a null entry, the restore points
    /// a coordinate (RRF sizes both from the axis counts) and the kinematics matrices a row and column
    /// (<see cref="M584ResizesTheKinematicsMatrices"/>)
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M584CreatesAnAxisWithTheDefaultsADriveStartsWith()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync($"M584 U{DriversRig.MainDriver(3)}")).Trim(), Is.Empty);
        Axis created = await bench.Host.ReadModelAsync(model => model.Move.Axes[3]);
        Assert.Multiple(() =>
        {
            Assert.That(created.Letter, Is.EqualTo('U'));
            Assert.That(created.Drivers, Is.EqualTo(new[] { new DriverId(DriversRig.MainDriverBoard, 3) }));
            Assert.That(created.Visible, Is.True, "a new axis is visible");
            Assert.That(created.Microstepping.Value, Is.EqualTo(16));
            Assert.That(created.Microstepping.Interpolated, Is.True,
                        "x16 interpolated, as Move::Init sets every drive (Move.cpp:534)");
            Assert.That(created.StepsPerMm, Is.EqualTo(80.0f));
            Assert.That(created.Speed, Is.EqualTo(6000.0f));
            Assert.That(created.Acceleration, Is.EqualTo(1000.0f));
            Assert.That(created.ReducedAcceleration, Is.EqualTo(1000.0f));
            Assert.That(created.Jerk, Is.EqualTo(900.0f));
            Assert.That(created.PrintingJerk, Is.EqualTo(900.0f));
            Assert.That(created.Min, Is.EqualTo(0.0f));
            Assert.That(created.Max, Is.EqualTo(200.0f));
            Assert.That(created.Current, Is.EqualTo(0));
            Assert.That(created.PercentCurrent, Is.EqualTo(100));
            Assert.That(created.PercentStstCurrent, Is.EqualTo(Axis.DefaultStandstillCurrentPercent),
                        "and the main board's default standstill percentage (Pins_Duet3_MB6HC.h)");
            Assert.That(created.PhaseStep, Is.False, "not phase stepping");
            Assert.That(created.Rotational, Is.False, "U is linear unless R or S says otherwise");
            Assert.That(created.ContinuousRotation, Is.False);
            Assert.That(created.Homed, Is.False);
            Assert.That(created.UserPosition, Is.EqualTo(0.0f));
            Assert.That(created.MachinePosition, Is.EqualTo(0.0f));
            Assert.That(created.WorkplaceOffsets, Is.EqualTo(new float[9]),
                        "a zero offset in each of the nine workplaces (RRF NumCoordinateSystems)");
        });
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes.Select(axis => axis.WorkplaceOffsets.Count).ToArray()),
                        Is.EqualTo(new[] { 9, 9, 9, 9 }), "as every axis has");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops.Count), Is.EqualTo(4),
                        "an endstop entry per axis");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[3]), Is.Null,
                        "which is null until M574 configures one");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.MotionSystems[0].RestorePoints.Select(point => point.Coords.Count).ToArray()),
                        Is.EqualTo(Enumerable.Repeat(4, DuetControlServer.Motion.RestorePoint.NumVisible)),
                        "and a coordinate for U in every restore point");
        });

        Assert.That(SentCanMessages(bench), Is.EqualTo(new[] { (DriversRig.MainDriverBoard, CanMessageType.SetStepsPerMmAndMicrostepping) }),
                    "the new driver is told its steps per mm and microstepping, and nothing else");
        CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping sent = bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>().Message;
        Assert.That((sent.DriversToUpdate, Values(sent)), Is.EqualTo(((ushort)0b1000, new[] { (80.0f, X16Interpolated) })));
    }

    /// <summary>
    /// What kind of axis M584 creates: R says whether it wraps at 360 degrees, S whether it counts as
    /// rotational in feedrate calculations, and without them the letter decides
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m584-rotational-axes.yaml</c>, whose report is <c>(r)(c)V3.0 W3.1 A3.2</c>
    /// for <c>V R1 S1</c>, <c>W R0 S0</c> and <c>A R0 S0</c>. RRF GCodes3.cpp DoDriveMapping: R is the
    /// wrap type, defaulting to wrapping for A to D; S defaults to whether the axis wraps
    /// (Move::SetAxisType)
    /// </remarks>
    [TestCase('U', "", false, false)]
    [TestCase('A', "", true, true)]
    [TestCase('D', "", true, true)]
    [TestCase('V', " R1 S1", true, true)]
    [TestCase('W', " R0 S0", false, false)]
    [TestCase('A', " R0 S0", false, false)]
    [TestCase('U', " R1", true, true)]
    [TestCase('B', " R0", false, false)]
    [TestCase('U', " R0 S1", true, false)]
    [TestCase('A', " S0", false, true)]
    public async Task M584RAndSSayWhatKindOfAxisIsCreated(char letter, string flags, bool rotational, bool continuous)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync($"M584 {letter}{DriversRig.MainDriver(3)}{flags}")).Trim(), Is.Empty);
        Axis created = await bench.Host.ReadModelAsync(model => model.Move.Axes[3]);
        Assert.Multiple(async () =>
        {
            Assert.That(created.Rotational, Is.EqualTo(rotational), "rotational");
            Assert.That(created.ContinuousRotation, Is.EqualTo(continuous), "continuous");
            Assert.That((await bench.Host.ExecuteCodeAsync("M584")).TrimEnd(),
                        Is.EqualTo($"Driver assignments: X{DriversRig.MainDriver(0)} Y{DriversRig.MainDriver(1)} Z{DriversRig.MainDriver(2)} "
                                   + $"{(rotational ? "(r)" : "")}{(continuous ? "(c)" : "")}{letter}{DriversRig.MainDriver(3)} E{DriversRig.ClosedLoopDriver(0)}, 4 axes visible"),
                        "the report marks a rotational axis (r) and a continuous one (c) before its drivers");
        });
    }

    /// <summary>
    /// R and S apply to every axis the code creates and to no axis that already exists
    /// </summary>
    /// <remarks>RRF GCodes3.cpp DoDriveMapping calls Move::SetAxisType only in the new axis branch</remarks>
    [Test]
    public async Task M584RAndSApplyOnlyToTheAxesTheCodeCreates()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync($"M584 X{DriversRig.MainDriver(3)} U{DriversRig.MainDriver(4)} V{DriversRig.MainDriver(5)} R1 S1")).Trim(), Is.Empty);
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes.Select(axis => (axis.Letter, axis.Rotational, axis.ContinuousRotation)).ToArray()),
                    Is.EqualTo(new[] { ('X', false, false), ('Y', false, false), ('Z', false, false), ('U', true, true), ('V', true, true) }));
    }

    /// <summary>
    /// An R or S out of range is refused before anything is created or mapped
    /// </summary>
    /// <remarks>RRF GCodes3.cpp DoDriveMapping reads both with GetLimitedUIValue (R below AxisWrapType::undefined, S below 2) before the axis letters</remarks>
    [TestCase("R2")]
    [TestCase("R-1")]
    [TestCase("S2")]
    [TestCase("S-1")]
    [Category("KnownGap")]
    public async Task M584RefusesAnROrSOutOfRange(string flag)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync($"M584 X{DriversRig.MainDriver(3)} U{DriversRig.MainDriver(4)} {flag}");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes.Select(axis => axis.Letter).ToArray()),
                        Is.EqualTo(new[] { 'X', 'Y', 'Z' }), "no axis is created");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Drivers[0]),
                        Is.EqualTo(new DriverId(DriversRig.MainDriverBoard, 0)), "and X keeps its driver");
            Assert.That(SentCanMessages(bench), Is.Empty);
        });
    }

    /// <summary>
    /// Axes created by one M584 are created in the order of the axis letters, not of the parameters
    /// </summary>
    /// <remarks>
    /// RRF GCodes3.cpp DoDriveMapping walks <c>AllowedAxisLetters</c>, "XYZUVWABCDabcdef...", and the
    /// wiki's M584 notes: "M584 C5 U6 creates axes U and C in that order"
    /// </remarks>
    [Test]
    public async Task M584CreatesAxesInLetterOrder()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync($"M584 C{DriversRig.MainDriver(3)} U{DriversRig.MainDriver(4)}")).Trim(), Is.Empty);
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes.Select(axis => axis.Letter).ToArray()),
                    Is.EqualTo(new[] { 'X', 'Y', 'Z', 'U', 'C' }));
    }

    /// <summary>
    /// Creating an axis makes every axis visible again, including any an earlier P hid
    /// </summary>
    /// <remarks>RRF GCodes3.cpp DoDriveMapping, new axis branch: <c>numVisibleAxes = numTotalAxes</c></remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M584CreatingAnAxisMakesEveryAxisVisible()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config, "M584 P2");

        Assert.That((await bench.Host.ExecuteCodeAsync($"M584 U{DriversRig.MainDriver(3)}")).Trim(), Is.Empty);
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes.Select(axis => axis.Visible).ToArray()),
                    Is.EqualTo(new[] { true, true, true, true }));

        Assert.That((await bench.Host.ExecuteCodeAsync($"M584 V{DriversRig.MainDriver(4)} P3")).Trim(), Is.Empty);
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes.Select(axis => axis.Visible).ToArray()),
                    Is.EqualTo(new[] { true, true, true, false, false }),
                    "unless the same code says how many are visible");
    }

    /// <summary>
    /// The kinematics matrices are as large as the machine's axes, and M584 resizes them
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m584-create-additional-axis.yaml</c>: creating U adds row and column 3 to
    /// both matrices, the identity there. RepRapFirmware sizes them from the total axis count as they
    /// are read (<c>CoreKinematics::objectModelArrayTable</c>, 3.7-dev), so hiding an axis with M584 P
    /// leaves both matrices square over every axis
    /// </remarks>
    [Test]
    public async Task M584ResizesTheKinematicsMatrices()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        async Task<(float[][] Inverse, float[][] Forward)> ReadMatricesAsync() => await bench.Host.ReadModelAsync(model =>
        {
            CoreKinematics core = (CoreKinematics)model.Move.Kinematics;
            return (core.InverseMatrix.Select(row => row.ToArray()).ToArray(), core.ForwardMatrix.Select(row => row.ToArray()).ToArray());
        });
        static float[][] Identity(int rows, int columns)
            => [.. Enumerable.Range(0, rows).Select(row => Enumerable.Range(0, columns).Select(column => row == column ? 1.0f : 0.0f).ToArray())];

        (float[][] inverse, float[][] forward) = await ReadMatricesAsync();
        Assert.Multiple(() =>
        {
            Assert.That(inverse, Is.EqualTo(Identity(3, 3)), "X, Y and Z, each on a motor of its own");
            Assert.That(forward, Is.EqualTo(Identity(3, 3)));
        });

        await bench.Host.ExecuteCodeAsync($"M584 U{DriversRig.MainDriver(3)}");
        (inverse, forward) = await ReadMatricesAsync();
        Assert.Multiple(() =>
        {
            Assert.That(inverse, Is.EqualTo(Identity(4, 4)), "U adds a row and a column, and a motor of its own");
            Assert.That(forward, Is.EqualTo(Identity(4, 4)));
        });

        await bench.Host.ExecuteCodeAsync("M584 P3");
        (inverse, forward) = await ReadMatricesAsync();
        Assert.Multiple(() =>
        {
            Assert.That(inverse, Is.EqualTo(Identity(4, 4)), "a hidden axis keeps its row and column");
            Assert.That(forward, Is.EqualTo(Identity(4, 4)));
        });
    }

    /// <summary>
    /// An M584 that creates an axis and then fails on a later parameter still leaves the axis, and the
    /// planner moves it
    /// </summary>
    /// <remarks>
    /// The axis loop writes the mapping before P is read, so the axis exists whether P is out of range
    /// or has no value. No other code runs
    /// between that M584 and the move, because any configuring code would bring the planner up to date
    /// on its own
    /// </remarks>
    [TestCaseSource(nameof(CreatingUThenFailing))]
    public async Task M584ThatFailsAfterCreatingAnAxisStillLetsItMove(string code)
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyze, Machines.OneTool, $"M569 P{Expansion.Driver(Machines.DriverBoard, UDriver)} S1");

        string reply = await bench.Host.ExecuteCodeAsync(code);
        float stepsPerMm = await bench.Host.ReadModelAsync(model => model.Move.Axes[3].StepsPerMm);
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error: "), "the code fails");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[3].Letter), Is.EqualTo('U'),
                        "but the axis it created stays");
        });

        await bench.Host.ExecuteCodeAsync("G91");
        IReadOnlyList<ScheduledMove> move = await bench.RunMoveAsync("G1 U5 F600");
        Assert.Multiple(() =>
        {
            Assert.That(move.DriversMoved(), Is.EqualTo(new[] { (Machines.DriverBoard, UDriver) }));
            Assert.That(move.Steps(UDriver), Is.EqualTo((int)MathF.Round(5 * stepsPerMm)));
        });
    }

    /// <summary>
    /// One axis can be driven by several drivers, and all of them get the axis' settings
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m584-multiple-drivers-per-axis.yaml</c>: <c>M584 Z1.0:1.1</c> sends one
    /// <c>setStepsPerMmAndMicrostepping</c> carrying Z's 400 steps per mm for both drivers, and M906
    /// and M350 then reach both
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M584GivesAnAxisTwoDrivers()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        float zStepsPerMm = await bench.Host.ReadModelAsync(model => model.Move.Axes[2].StepsPerMm);

        Assert.That((await bench.Host.ExecuteCodeAsync($"M584 Z{DriversRig.MainDriver(3)}:{DriversRig.MainDriver(4)}")).Trim(), Is.Empty);

        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[2].Drivers.ToArray()),
                    Is.EqualTo(new[] { new DriverId(DriversRig.MainDriverBoard, 3), new DriverId(DriversRig.MainDriverBoard, 4) }));
        Assert.That(SentCanMessages(bench), Is.EqualTo(new[] { (DriversRig.MainDriverBoard, CanMessageType.SetStepsPerMmAndMicrostepping) }));
        CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping steps = bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>().Message;
        Assert.That((steps.DriversToUpdate, Values(steps)),
                    Is.EqualTo(((ushort)0b11000, new[] { (zStepsPerMm, X16Interpolated), (zStepsPerMm, X16Interpolated) })),
                    "both drivers get the axis' one steps per mm, in one request");

        await bench.Host.ExecuteCodeAsync("M906 Z400");
        var currents = bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestMotorCurrents>();
        Assert.That(currents[^1].Message.DriversToUpdate, Is.EqualTo(0b11000),
                    "the axis' current reaches both of its drivers in one request, because it is one "
                    + "drive (RRF Move2.cpp SetMotorCurrent iterates the drive's drivers)");

        await bench.Host.ExecuteCodeAsync("M350 Z16 I1");
        Assert.That(bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>().Message.DriversToUpdate, Is.EqualTo(0b11000),
                    "and so does its microstepping");

        Assert.That((await bench.Host.ExecuteCodeAsync("M584")).TrimEnd(),
                    Is.EqualTo($"Driver assignments: X{DriversRig.MainDriver(0)} Y{DriversRig.MainDriver(1)} Z{DriversRig.MainDriver(3)}:{DriversRig.MainDriver(4)} E{DriversRig.ClosedLoopDriver(0)}, 3 axes visible"),
                    "and the report joins them with a colon");
    }

    /// <summary>
    /// More drivers than an axis or the extruders can have is refused, and nothing of the code is applied
    /// </summary>
    /// <remarks>
    /// RRF GCodeBuffer GetDriverIdArray throws once the list is longer than MaxDriversPerAxis (8) or
    /// MaxExtruders (20). RRF has applied the axis letters before the one that throws; DSF reads the
    /// whole code first, as for any array parameter it refuses (rrf-differences.md section 6.1)
    /// </remarks>
    [TestCase('U', 9)]
    [TestCase('E', 21)]
    public async Task M584RefusesTooManyDrivers(char letter, int count)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string drivers = string.Join(':', Enumerable.Range(0, count).Select(driver => Expansion.Driver(OtherBoard, driver % 16)));
        string reply = await bench.Host.ExecuteCodeAsync($"M584 X{DriversRig.MainDriver(3)} {letter}{drivers}");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes.Count), Is.EqualTo(3));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders.Count), Is.EqualTo(1));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Drivers[0]),
                        Is.EqualTo(new DriverId(DriversRig.MainDriverBoard, 0)));
            Assert.That(SentCanMessages(bench), Is.Empty);
        });
    }

    /// <summary>
    /// The parameters that map the given axis letters, each on a driver of its own on boards no rig uses,
    /// with a lower case letter quoted as G-code requires
    /// </summary>
    private static string MapLetters(IEnumerable<char> letters)
        => string.Join(' ', letters.Select((letter, i) => (char.IsLower(letter) ? "'" : "") + char.ToUpperInvariant(letter)
                                                          + Expansion.Driver((byte)(10 + i / 16), i % 16)));

    /// <summary>
    /// A lower case axis is named with a quote, and a quoted G, M or T is an axis rather than the start
    /// of another code
    /// </summary>
    /// <remarks>
    /// The wiki's M584 notes: "axes that are represented by lowercase letters must be prefixed with a
    /// single quote character", and RRF GCodes.h AllowedAxisLetters includes g, m and t on Duet 3
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M584CreatesLowerCaseAxes()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync($"M584 'A{DriversRig.MainDriver(3)} 'G{DriversRig.MainDriver(4)} 'm{DriversRig.MainDriver(5)}")).Trim(), Is.Empty);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes.Select(axis => axis.Letter).ToArray()),
                        Is.EqualTo(new[] { 'X', 'Y', 'Z', 'a', 'g', 'm' }));
            Assert.That((await bench.Host.ExecuteCodeAsync("M584")).TrimEnd(),
                        Is.EqualTo($"Driver assignments: X{DriversRig.MainDriver(0)} Y{DriversRig.MainDriver(1)} Z{DriversRig.MainDriver(2)} "
                                   + $"a{DriversRig.MainDriver(3)} g{DriversRig.MainDriver(4)} m{DriversRig.MainDriver(5)} E{DriversRig.ClosedLoopDriver(0)}, 6 axes visible"),
                        "a lower case axis is linear: only A to D default to rotational");
        });
    }

    /// <summary>
    /// M584 refuses to create more axes than the machine can have, or more axes and extruders together
    /// than it has drives for, and applies nothing of the code
    /// </summary>
    /// <remarks>
    /// RRF GCodes3.cpp DoDriveMapping ignores an axis letter once there are MaxAxes (30 on a Duet 3
    /// MB6HC) without saying so, and drops the last extruder when a new axis would take its drive
    /// (<c>numTotalAxes + numExtruders &gt; MaxAxesPlusExtruders</c>, 32). DSF refuses both, as RRF
    /// refuses a list of too many drivers (rrf-differences.md section 3.1)
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M584RefusesMoreAxesThanTheMachineCanHave()
    {
        const int maxAxes = 30, maxDrives = 32;
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync("M584 " + MapLetters(Axis.Letters.Skip(3).Take(maxAxes - 3)))).Trim(), Is.Empty);
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes.Count), Is.EqualTo(maxAxes));

        bench.CanMaster.ClearCapture();
        string reply = await bench.Host.ExecuteCodeAsync($"M584 X{DriversRig.MainDriver(3)} {MapLetters(Axis.Letters.Skip(maxAxes).Take(1))}");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain("Too many axes"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes.Count), Is.EqualTo(maxAxes), "no axis is created");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Drivers[0]),
                        Is.EqualTo(new DriverId(DriversRig.MainDriverBoard, 0)), "and X keeps its driver");
            Assert.That(SentCanMessages(bench), Is.Empty);
        });

        string extruders = string.Join(':', Enumerable.Range(0, maxDrives - maxAxes + 1).Select(i => Expansion.Driver(OtherBoard, i)));
        reply = await bench.Host.ExecuteCodeAsync($"M584 E{extruders}");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain("Too many axes and extruders"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders.Count), Is.EqualTo(1), "the extruders are as they were");
        });

        extruders = string.Join(':', Enumerable.Range(0, maxDrives - maxAxes).Select(i => Expansion.Driver(OtherBoard, i)));
        Assert.That((await bench.Host.ExecuteCodeAsync($"M584 E{extruders}")).Trim(), Is.Empty, "and as many as there are drives left is fine");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders.Count), Is.EqualTo(maxDrives - maxAxes));
    }

    /// <summary>
    /// A driver that a board on the bus does not have is refused, naming it, and nothing of the code is applied
    /// </summary>
    /// <remarks>
    /// RRF GCodes3.cpp DoDriveMapping replies "Driver 5.3 does not exist" for a driver at or past the
    /// board's driver count, drops it from the list and applies the rest. DSF refuses the whole code,
    /// as for any array element it refuses, and checks the extruders' drivers as it does the axes',
    /// which RRF does not (rrf-differences.md section 3.1)
    /// </remarks>
    [TestCase("U{0}")]
    [TestCase("U{1}:{0}")]
    [TestCase("E{0}")]
    public async Task M584RefusesADriverTheBoardDoesNotHave(string mapping)
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        bench.CanMaster.InjectAnnounce(AnnouncedBoard, "EXP3HC", "3.7.0", "2026-09-08", numDrivers: AnnouncedBoardDrivers);
        await bench.Host.WaitForModelAsync(model => model.FindBoard(AnnouncedBoard)?.MaxMotors, AnnouncedBoardDrivers,
                                           $"driver count of board {AnnouncedBoard}");
        bench.CanMaster.ClearCapture();

        string missing = Expansion.Driver(AnnouncedBoard, AnnouncedBoardDrivers);
        string reply = await bench.Host.ExecuteCodeAsync("M584 " + string.Format(mapping, missing, Expansion.Driver(AnnouncedBoard, 0)));
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain($"Driver {missing} does not exist"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes.Count), Is.EqualTo(3), "no axis is created");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders.Select(extruder => extruder.Driver).ToArray()),
                        Is.EqualTo(new[] { new DriverId(DriversRig.ClosedLoopBoard, 0) }), "and the extruder keeps its driver");
            Assert.That(SentCanMessages(bench), Is.Empty);
        });
    }

    /// <summary>
    /// A driver on a board that is not on the bus is accepted, the axes either side of it are created
    /// and mapped, and the board's failure to answer is reported at the end
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m584-expansion-timeout.yaml</c>: <c>M584 U3.0 V99.0 W3.1 A2.0</c> creates
    /// all four axes, sends one <c>setStepsPerMmAndMicrostepping</c> per board in ascending board
    /// address order (2, 3, 99: RRF's CanDriversData keeps its entries sorted by driver id), and
    /// replies "Timed out awaiting response from board 99 to request type 6042" as an error. The M92
    /// that follows reaches every axis and times out on board 99 again
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M584AcceptsADriverOnABoardThatIsNotOnTheBus()
    {
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster =>
                                             {
                                                 ScriptedCanMaster.CanMessageHandler answer = canMaster.DefaultCanHandler!;
                                                 canMaster.DefaultCanHandler = (fake, header, payload) =>
                                                 {
                                                     if (header.DstAddress != AbsentBoard)
                                                     {
                                                         answer(fake, header, payload);
                                                     }
                                                 };
                                             })
                                             .StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M584 U{DriversRig.MainDriver(3)} V{Expansion.Driver(AbsentBoard, 0)} W{DriversRig.MainDriver(4)} A{Expansion.Driver(OtherBoard, 0)}");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error"));
            Assert.That(reply.TrimEnd().Split('\n'), Has.Length.EqualTo(1), "one board did not answer one request");
            Assert.That(reply, Does.Contain($"board {AbsentBoard}, req type {(int)CanMessageType.SetStepsPerMmAndMicrostepping}"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes.Skip(3).Select(axis => (axis.Letter, axis.Drivers.Single())).ToArray()),
                        Is.EqualTo(new[]
                        {
                            ('U', new DriverId(DriversRig.MainDriverBoard, 3)),
                            ('V', new DriverId(AbsentBoard, 0)),
                            ('W', new DriverId(DriversRig.MainDriverBoard, 4)),
                            ('A', new DriverId(OtherBoard, 0)),
                        }), "every axis is created and mapped, the one behind the absent board included");
            Assert.That(SentCanMessages(bench), Is.EqualTo(new[]
                        {
                            (DriversRig.MainDriverBoard, CanMessageType.SetStepsPerMmAndMicrostepping),
                            (OtherBoard, CanMessageType.SetStepsPerMmAndMicrostepping),
                            (AbsentBoard, CanMessageType.SetStepsPerMmAndMicrostepping),
                        }), "one request per board, in board address order");
            Assert.That(bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>()[0].Message.DriversToUpdate,
                        Is.EqualTo(0b11000), "U and W share a board, and a request");
        });

        bench.CanMaster.ClearCapture();
        const float stepsPerMm = 100.0f;
        reply = await bench.Host.ExecuteCodeAsync($"M92 U{stepsPerMm} V{stepsPerMm} W{stepsPerMm} A{stepsPerMm}");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain($"board {AbsentBoard}, req type {(int)CanMessageType.SetStepsPerMmAndMicrostepping}"));
            Assert.That(SentCanMessages(bench).Select(message => message.Board), Is.EqualTo(new[] { DriversRig.MainDriverBoard, OtherBoard, AbsentBoard }),
                        "the new axes are usable: M92 reaches every one of them");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes.Skip(3).Select(axis => axis.StepsPerMm).ToArray()),
                        Is.EqualTo(Enumerable.Repeat(stepsPerMm, 4)));
        });
    }

    /// <summary>
    /// M584 reports its mapping, creates an axis the first time a letter is named, refuses a driver
    /// that is already owned, and a bare letter releases the axis' drivers
    /// </summary>
    /// <remarks>
    /// RRF GCodes3.cpp GCodes::DoDriveMapping: a new letter appends an axis and assigns its drivers,
    /// visible by default. The refusal of a driver owned by another drive and the release through a
    /// bare letter are rrf-differences.md section 3.1 (RRF checks only that the driver exists and
    /// never shrinks a mapping)
    /// </remarks>
    [Test]
    public async Task M584MapsCreatesAndReleasesAxes()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye);

        string report = (await bench.Host.ExecuteCodeAsync("M584")).Trim();
        Assert.Multiple(() =>
        {
            Assert.That(report, Does.StartWith("Driver assignments:"),
                        "bare M584 reports the driver assignments (RRF GCodes3.cpp DoDriveMapping report branch)");
            Assert.That(report, Does.Contain($"X{Expansion.Driver(Machines.DriverBoard, Machines.XDriver)}")
                                    .And.Contain($"Y{Expansion.Driver(Machines.DriverBoard, Machines.YDriver)}")
                                    .And.Contain($"E{Expansion.Driver(Machines.DriverBoard, Machines.EDriver)}"),
                        "the bare M584 report names the drivers the object model holds for X, Y and E");
        });

        await bench.Host.ExecuteCodeAsync($"M584 U{Expansion.Driver(Machines.DriverBoard, UDriver)}");
        Axis axisU = await bench.Host.ReadModelAsync(model => model.Move.Axes[2]);
        Extruder extruder = await bench.Host.ReadModelAsync(model => model.Move.Extruders[0]);

        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.Axes.Count).Result, Is.EqualTo(3),
                        "M584 U creates a third axis (RRF GCodes3.cpp DoDriveMapping, new axis branch)");
            Assert.That(axisU.Letter, Is.EqualTo('U'),
                        "the new axis is U (RRF GCodes3.cpp DoDriveMapping)");
            Assert.That(axisU.Drivers[0], Is.EqualTo(new DuetAPI.Utility.DriverId(Machines.DriverBoard, UDriver)),
                        $"M584 U{Expansion.Driver(Machines.DriverBoard, UDriver)} sets move.axes[2].drivers[0] (RRF GCodes3.cpp DoDriveMapping, SetAxisDriversConfig)");
            Assert.That(axisU.Visible, Is.True,
                        "a new axis is visible by default (RRF GCodes3.cpp DoDriveMapping, numVisibleAxes = numTotalAxes)");
            Assert.That(extruder.Driver, Is.EqualTo(new DuetAPI.Utility.DriverId(Machines.DriverBoard, Machines.EDriver)),
                        "the extruder mapping from config.g is untouched (RRF Move.cpp extruders table, extruderDrivers)");
        });

        string conflict = await bench.Host.ExecuteCodeAsync($"M584 V{Expansion.Driver(Machines.DriverBoard, Machines.XDriver)}");
        Assert.That(conflict, Does.Contain(Expansion.Driver(Machines.DriverBoard, Machines.XDriver)).And.Contain("already used"),
                    "M584 refuses a driver another axis owns, naming it (rrf-differences.md section 3.1)");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes.Count), Is.EqualTo(3),
                    "the refused mapping creates no axis (rrf-differences.md section 3.1)");

        await bench.Host.ExecuteCodeAsync("M584 U");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.Axes.Count).Result, Is.EqualTo(3),
                        "M584 U keeps the axis in move.axes[], positions and indices do not move (rrf-differences.md section 3.1)");
            Assert.That(axisU.Drivers.Count, Is.EqualTo(0),
                        "M584 U releases the drivers of U (rrf-differences.md section 3.1)");
        });

        await bench.Host.ExecuteCodeAsync($"M584 U{Expansion.Driver(Machines.DriverBoard, UDriver)}:{Expansion.Driver(Machines.DriverBoard, SecondUDriver)}");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.Axes[2].Drivers.Count).Result, Is.EqualTo(2));
            Assert.That(axisU.Drivers[0], Is.EqualTo(new DuetAPI.Utility.DriverId(Machines.DriverBoard, UDriver)));
            Assert.That(axisU.Drivers[1], Is.EqualTo(new DuetAPI.Utility.DriverId(Machines.DriverBoard, SecondUDriver)));
        });
    }
}
