using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;
using System.Globalization;
using System.Threading;

namespace SystemTests.Scenarios.Codes.Drivers;

/// <summary>M584, mapping drivers to axes and extruders, which also creates and hides axes</summary>
[TestFixture]
[GCode("M584")]
public class M584Tests : BenchFixture
{
    /// <summary>Drivers of <see cref="Machines.DriverBoard"/> that no machine maps, for the U axis the machine scenarios create</summary>
    private const byte UDriver = 4, SecondUDriver = 5;

    /// <summary>M584s that create U and then fail on P, out of range or with no value</summary>
    private static readonly string[] CreatingUThenFailing = [$"M584 U{Expansion.Driver(Machines.DriverBoard, UDriver)} P9", $"M584 U{Expansion.Driver(Machines.DriverBoard, UDriver)} P"];

    /// <summary>
    /// M584 maps axes onto an expansion board's drivers, records the mapping and puts each driver
    /// back into step/direction mode
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m584-map-axes-to-expansion.yaml</c> and <c>m584-map-extruder.yaml</c>. The
    /// step mode message is RepRapFirmware's (GCodes3.cpp:545 and :569, one
    /// <c>Move::SetStepMode(drive, StepMode::stepDir, reply)</c> per drive): a driver moved to another
    /// axis would otherwise keep the phase stepping mode the axis before it left it in
    /// </remarks>
    [Test]
    public async Task M584MapsAxesAndResetsTheStepMode()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config, $"M584 E{DriversRig.ClosedLoopDriver(0)}:{DriversRig.ClosedLoopDriver(1)}:{DriversRig.ClosedLoopDriver(2)}");

        Assert.That((await bench.Host.ExecuteCodeAsync("M970 X1 E0:1:0")).Trim(), Is.Empty);
        bench.CanMaster.ClearCapture();

        Assert.That((await bench.Host.ExecuteCodeAsync($"M584 X{DriversRig.MainDriver(3)} Y{DriversRig.MainDriver(4)} Z{DriversRig.MainDriver(5)} E{DriversRig.ClosedLoopDriver(3)}")).Trim(), Is.Empty);

        var stepModes = bench.CanMaster.CanMessages<CanMessageM970>();
        Assert.That(stepModes.Select(message => (message.Board, message.Message.P, message.Message.S)),
                    Is.EqualTo(new[]
                    {
                        (DriversRig.MainDriverBoard, (byte?)3, (byte?)0),
                        (DriversRig.MainDriverBoard, (byte?)4, (byte?)0),
                        (DriversRig.MainDriverBoard, (byte?)5, (byte?)0),
                        (DriversRig.ClosedLoopBoard, (byte?)3, (byte?)0),
                    }),
                    "reset the new drivers step mode");

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Drivers[0]),
                        Is.EqualTo(new DriverId(DriversRig.MainDriverBoard, 3)));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[2].Drivers[0]),
                        Is.EqualTo(new DriverId(DriversRig.MainDriverBoard, 5)));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].PhaseStep), Is.False,
                        "and the drive is no longer phase stepping, whatever it was before");
        });
    }

    /// <summary>
    /// A board that never answers the step-mode reset does not make M584 fail, but is reported
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m584-map-axes-to-expansion.yaml</c>, whose reply is three CAN timeouts at
    /// <em>ok</em> severity: RepRapFirmware discards what <c>Move::SetStepMode</c> returns
    /// (GCodes3.cpp:545) while the reply buffer it was given keeps the text. The mapping is already
    /// done, and undoing it would leave the machine in neither state.
    /// DCS returns an error on a CAN timeout which is a deviation from RRF, the error doesn't stop the command from finishing though as per RRF
    /// </remarks>
    [Test]
    [Category("LongRunning")]
    public async Task M584ReportsAStepModeResetThatWentUnanswered()
    {
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.NeverAnswer<CanMessageM970>())
                                             .StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M584 X{DriversRig.MainDriver(3)} Y{DriversRig.MainDriver(4)}");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error"), "the mapping stands");
            Assert.That(reply.TrimEnd().Split('\n'), Has.Length.EqualTo(2),
                        "one timeout line per drive, because a drive stops at the first driver that "
                        + "did not answer");
            Assert.That(reply, Does.Contain($"CAN response timeout: board {DriversRig.MainDriverBoard}, req type 6072"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Drivers[0]),
                        Is.EqualTo(new DriverId(DriversRig.MainDriverBoard, 3)),
                        "and the drivers are the ones M584 named");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[1].Drivers[0]),
                        Is.EqualTo(new DriverId(DriversRig.MainDriverBoard, 4)),
                        "and the drivers are the ones M584 named");
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
    /// M584 P hides the axes past the count it is given, without taking their drivers away
    /// </summary>
    /// <remarks><c>testcases/drivers/m584-hide-axis.yaml</c></remarks>
    [Test]
    public async Task M584PHidesAxes()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        await bench.Host.ExecuteCodeAsync("M584 P2");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[2].Visible), Is.False,
                        "P2 leaves X and Y visible and hides Z");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[2].Drivers.Count), Is.EqualTo(1),
                        "a hidden axis keeps its drivers");
        });

        Assert.That((await bench.Host.ExecuteCodeAsync("M584")).TrimEnd(),
                    Is.EqualTo($"Driver assignments: X{DriversRig.MainDriver(0)} Y{DriversRig.MainDriver(1)} Z{DriversRig.MainDriver(2)} E{DriversRig.ClosedLoopDriver(0)}, 2 axes visible"));
    }

    /// <summary>
    /// M584 creates an axis the first time a letter is named, with the defaults a drive starts life
    /// with, and R and S say what kind of axis it is
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m584-create-additional-axis.yaml</c> and
    /// <c>m584-rotational-axes.yaml</c>. The reference's delta for a new axis includes
    /// <c>microstepping.interpolated true</c>, <c>percentStstCurrent 71</c> and
    /// <c>phaseStep false</c>, which are what Move::Init gives every drive
    /// </remarks>
    [Test]
    public async Task M584CreatesAnAxisWithTheDefaultsADriveStartsWith()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        await bench.Host.ExecuteCodeAsync($"M584 U{DriversRig.MainDriver(3)}");
        Axis created = await bench.Host.ReadModelAsync(model => model.Move.Axes[3]);
        Assert.Multiple(() =>
        {
            Assert.That(created.Letter, Is.EqualTo('U'));
            Assert.That(created.Visible, Is.True, "a new axis is visible");
            Assert.That(created.Microstepping.Value, Is.EqualTo(16));
            Assert.That(created.Microstepping.Interpolated, Is.True,
                        "x16 interpolated, as Move::Init sets every drive (Move.cpp:534)");
            Assert.That(created.PercentCurrent, Is.EqualTo(100));
            Assert.That(created.PercentStstCurrent, Is.EqualTo(Axis.DefaultStandstillCurrentPercent),
                        "and the main board's default standstill percentage (Pins_Duet3_MB6HC.h)");
            Assert.That(created.PhaseStep, Is.False, "not phase stepping");
            Assert.That(created.Homed, Is.False);
            Assert.That(created.WorkplaceOffsets, Is.EqualTo(new float[9]),
                        "a zero offset in each of the nine workplaces (RRF NumCoordinateSystems)");
        });
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes.Select(axis => axis.WorkplaceOffsets.Count).ToArray()),
                    Is.EqualTo(new[] { 9, 9, 9, 9 }), "as every axis has");

        await bench.Host.ExecuteCodeAsync($"M584 V{DriversRig.MainDriver(4)} R1 S1");
        Assert.That((await bench.Host.ExecuteCodeAsync("M584")).TrimEnd(),
                    Is.EqualTo($"Driver assignments: X{DriversRig.MainDriver(0)} Y{DriversRig.MainDriver(1)} Z{DriversRig.MainDriver(2)} U{DriversRig.MainDriver(3)} (r)(c)V{DriversRig.MainDriver(4)} E{DriversRig.ClosedLoopDriver(0)}, 5 axes visible"),
                    "the report marks a rotational axis (r) and a continuous one (c) before its "
                    + "drivers (RRF GCodes3.cpp DoDriveMapping report branch)");
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
    /// One axis can be driven by two drivers, and both get the axis' settings
    /// </summary>
    /// <remarks><c>testcases/drivers/m584-multiple-drivers-per-axis.yaml</c></remarks>
    [Test]
    public async Task M584GivesAnAxisTwoDrivers()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        await bench.Host.ExecuteCodeAsync($"M584 Z{DriversRig.MainDriver(2)}:{DriversRig.MainDriver(3)}");

        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[2].Drivers.ToArray()),
                    Is.EqualTo(new[] { new DriverId(DriversRig.MainDriverBoard, 2), new DriverId(DriversRig.MainDriverBoard, 3) }));

        await bench.Host.ExecuteCodeAsync("M906 Z400");
        var currents = bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestMotorCurrents>();
        Assert.That(currents[^1].Message.DriversToUpdate, Is.EqualTo(0b1100),
                    "the axis' current reaches both of its drivers in one request, because it is one "
                    + "drive (RRF Move2.cpp SetMotorCurrent iterates the drive's drivers)");

        Assert.That((await bench.Host.ExecuteCodeAsync("M584")).TrimEnd(),
                    Is.EqualTo($"Driver assignments: X{DriversRig.MainDriver(0)} Y{DriversRig.MainDriver(1)} Z{DriversRig.MainDriver(2)}:{DriversRig.MainDriver(3)} E{DriversRig.ClosedLoopDriver(0)}, 3 axes visible"),
                    "and the report joins them with a colon");
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
