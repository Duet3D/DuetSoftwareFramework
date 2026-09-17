using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Codes;

/// <summary>
/// Drive and axis configuration M-codes and the object model fields each one must set. The expected
/// behaviour is RepRapFirmware's (lib/RepRapFirmware), except where
/// src/Documentation/articles/rrf-differences.md documents a deliberate deviation, which is then the
/// behaviour asserted and cited.
/// M569.6 is omitted: it runs a closed-loop tuning move whose outcome is judged by the driver, and the
/// fake controller answers every request with an empty StandardReply, so it cannot script one. The
/// register form of M569.2 is omitted for the same reason - its whole result is the register value the
/// board replies with - but the waveform correction form added at 3.7 sets rather than reads, so what
/// it puts on the bus is assertable
/// </summary>
[TestFixture]
public class DriveConfigCodeTests : SystemTests.Host.BenchFixture
{
    /// <summary>
    /// M18 with an axis letter de-energises that axis' drivers and marks the axis not homed; a bare
    /// M84 does the same for every axis; M17 re-energises without touching the homed flags
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp cases 17/18/84: a named axis gets SetAxisNotHomed + DisableDrivers, a bare
    /// M18/M84 calls GCodes::DisableDrives (GCodes.cpp), which is DisableAllDrivers +
    /// SetAllAxesNotHomed. M17 only calls EnableDrivers
    /// </remarks>
    [Test]
    public async Task M17M18M84DriveEnableAndHomedFlags()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        Axis axis0 = await bench.Host.ReadModelAsync(model => model.Move.Axes[0]);
        Axis axis1 = await bench.Host.ReadModelAsync(model => model.Move.Axes[1]);

        Assert.That(axis0.Homed, Is.True,
                    "the bench config's G92 marks X homed");

        await bench.Host.ExecuteCodeAsync("M18 X");
        Assert.Multiple(() =>
        {
            Assert.That(axis0.Homed, Is.False,
                        "M18 X clears move.axes[0].homed (RRF GCodes2.cpp case 18, SetAxisNotHomed)");
            Assert.That(axis1.Homed, Is.True,
                        "M18 X leaves move.axes[1].homed alone, Y was not named");
        });

        await bench.Host.ExecuteCodeAsync("G92 X0");
        Assert.That(axis0.Homed, Is.True,
                    "G92 X marks X homed again");

        await bench.Host.ExecuteCodeAsync("M84");
        Assert.Multiple(() =>
        {
            Assert.That(axis0.Homed, Is.False,
                        "bare M84 clears move.axes[0].homed (RRF GCodes.cpp DisableDrives, SetAllAxesNotHomed)");
            Assert.That(axis1.Homed, Is.False,
                        "bare M84 clears move.axes[1].homed (RRF GCodes.cpp DisableDrives, SetAllAxesNotHomed)");
        });

        string reply = await bench.Host.ExecuteCodeAsync("M17");
        Assert.Multiple(() =>
        {
            Assert.That(reply.Trim(), Is.Empty, "M17 succeeds silently (RRF GCodes2.cpp case 17)");
            Assert.That(axis0.Homed, Is.False,
                        "M17 does not mark an axis homed (RRF GCodes2.cpp case 17 only enables drivers)");
        });
    }

    /// <summary>
    /// M84 S sets the idle timeout without de-energising anything: S makes the code 'seen', so the
    /// disable-everything branch never runs and the homed flags stay
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 84: gb.Seen('S') sets seen and calls Move::SetIdleTimeout; DisableDrives
    /// only runs when nothing at all was seen. move.idle.timeout is reported in seconds
    /// (Move.cpp idle table, 0.001f * idleTimeout)
    /// </remarks>
    [Test]
    public async Task M84SSetsIdleTimeoutWithoutDisabling()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        await bench.Host.ExecuteCodeAsync("M84 S45");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Idle.Timeout), Is.EqualTo(45.0).Within(1e-3),
                        "M84 S sets move.idle.timeout in seconds (RRF GCodes2.cpp case 84, SetIdleTimeout)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Homed), Is.True,
                        "M84 S does not disable the motors, so X stays homed (RRF GCodes2.cpp case 84, seen branch)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[1].Homed), Is.True,
                        "M84 S does not disable the motors, so Y stays homed (RRF GCodes2.cpp case 84, seen branch)");
        });
    }

    /// <summary>M201 sets the normal acceleration of axes and extruders, in mm/s^2</summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 201 with fraction 0 and Move2.cpp Move::SetAcceleration(..., false);
    /// move.axes[].acceleration is reported through InverseConvertAcceleration in mm/s^2
    /// (Move.cpp axes table)
    /// </remarks>
    [Test]
    public async Task M201SetsAcceleration()
    {
        // TODO test setting multiple extruders
        await using JobBench bench = await JobControlBench.StartAsync();

        await bench.Host.ExecuteCodeAsync("M201 X1250 Y1100 E3000");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Acceleration), Is.EqualTo(1250.0).Within(1e-2),
                        "M201 X sets move.axes[0].acceleration (mm/s^2, RRF Move2.cpp SetAcceleration)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[1].Acceleration), Is.EqualTo(1100.0).Within(1e-2),
                        "M201 Y sets move.axes[1].acceleration (mm/s^2, RRF Move2.cpp SetAcceleration)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Acceleration), Is.EqualTo(3000.0).Within(1e-2),
                        "M201 E sets move.extruders[0].acceleration (mm/s^2, RRF Move2.cpp SetAcceleration)");
        });
    }

    /// <summary>
    /// M201.1 sets the reduced acceleration used by probing and stall homing moves, stored
    /// independently of the normal acceleration
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 201 with fraction 1 writes reducedAccelerations[] without clamping
    /// (Move2.cpp SetAcceleration). RRF's report takes min(reduced, normal) (Move.h inline
    /// Acceleration); the stored value is what is asserted here, see the TODO below. The bench
    /// config runs M201 X500
    /// </remarks>
    [Test]
    public async Task M201Dot1SetsReducedAcceleration()
    {
        // TODO test setting multiple extruders
        await using JobBench bench = await JobControlBench.StartAsync();

        double acceleration = await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Acceleration);
        await bench.Host.ExecuteCodeAsync("M201.1 X55");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.Axes[0].ReducedAcceleration).Result, Is.EqualTo(55.0).Within(1e-2),
                        "M201.1 X sets move.axes[0].reducedAcceleration (mm/s^2, RRF GCodes2.cpp case 201 frac 1)");
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.Axes[0].Acceleration).Result, Is.EqualTo(acceleration).Within(1e-2),
                        "M201.1 leaves move.axes[0].acceleration alone (RRF Move2.cpp SetAcceleration reduced branch)");
        });

        // TODO possible RRF bug where it allows reducedAcceleration to be greater than normalAcceleration
        await bench.Host.ExecuteCodeAsync("M201.1 X800");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].ReducedAcceleration), Is.EqualTo(800).Within(1e-2),
                    "M201.1 X800 stores 800 even above the normal acceleration (RRF Move2.cpp SetAcceleration does not clamp)");
    }

    /// <summary>
    /// M203 sets the minimum and maximum speeds. The parameter is mm/min by default and mm/s with S1, and the
    /// object model reports mm/min either way
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 203 (GetSpeedFromMm(usingMmPerSec)); move.axes[].speed is reported
    /// through InverseConvertSpeedToMmPerMin (Move.cpp axes table)
    /// </remarks>
    [Test]
    public async Task M203SetSpeeds()
    {
        // TODO test setting multiple extruders
        await using JobBench bench = await JobControlBench.StartAsync();

        await bench.Host.ExecuteCodeAsync("M203 X9000 E4200");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.Axes[0].Speed).Result, Is.EqualTo(9000.0).Within(1e-2),
                        "M203 X sets move.axes[0].speed (mm/min, RRF Move.cpp maxFeedrate)");
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Speed).Result, Is.EqualTo(4200.0).Within(1e-2),
                        "M203 E sets move.extruders[0].speed (mm/min, RRF Move.cpp maxFeedrate)");
        });

        await bench.Host.ExecuteCodeAsync("M203 Y100 S1");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[1].Speed), Is.EqualTo(6000.0).Within(1e-2),
                    "M203 Y100 S1 is 100 mm/s, reported as 6000 mm/min in move.axes[1].speed (RRF GCodes2.cpp case 203 usingMmPerSec)");

        await bench.Host.ExecuteCodeAsync("M203 I10 S1");
        Assert.That(bench.Host.ReadModelAsync(model => model.Move.MinimumMovementSpeed).Result, Is.EqualTo(10).Within(1e-2),
                    "M203 I10 S1 sets the minimum movement speed to 10 mm/s");


        {
            double minSpeed = 300;
            await bench.Host.ExecuteCodeAsync($"M203 X900 Y200 E200 I{minSpeed}");
            Assert.Multiple(() =>
            {
                Assert.That(bench.Host.ReadModelAsync(model => model.Move.Axes[0].Speed).Result, Is.EqualTo(900.0).Within(1e-2),
                            "M203 X sets move.axes[0].speed (mm/min, RRF Move.cpp maxFeedrate)");
                Assert.That(bench.Host.ReadModelAsync(model => model.Move.Axes[1].Speed).Result, Is.EqualTo(minSpeed).Within(1e-2),
                            "Y max speed is clamped by the min speed");
                Assert.That(bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Speed).Result, Is.EqualTo(minSpeed).Within(1e-2),
                            "E max speed is clamped by the min speed");
            });
        }
    }

    /// <summary>
    /// M204 sets the per-move acceleration limits: P for printing moves, T for travel moves, and S
    /// sets both for Marlin compatibility
    /// </summary>
    /// <remarks>
    /// RRF GCodes5.cpp GCodes::ConfigureAccelerations; move.printingAcceleration and
    /// move.travelAcceleration are reported in mm/s^2 (Move.cpp table)
    /// </remarks>
    [Test]
    public async Task M204SetsPrintingAndTravelAcceleration()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        // Default accelerations
#pragma warning disable CS0618
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.PrintingAcceleration), Is.EqualTo(Move.DefaultPrintingAcceleration).Within(1e-2));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.TravelAcceleration), Is.EqualTo(Move.DefaultTravelAcceleration).Within(1e-2));
        });

        await bench.Host.ExecuteCodeAsync("M204 P900 T1600");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.PrintingAcceleration).Result, Is.EqualTo(900.0).Within(1e-2),
                        "M204 P sets move.printingAcceleration (mm/s^2, RRF GCodes5.cpp ConfigureAccelerations)");
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.TravelAcceleration).Result, Is.EqualTo(1600.0).Within(1e-2),
                        "M204 T sets move.travelAcceleration (mm/s^2, RRF GCodes5.cpp ConfigureAccelerations)");
        });

        await bench.Host.ExecuteCodeAsync("M204 S700");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.PrintingAcceleration).Result, Is.EqualTo(700.0).Within(1e-2),
                        "M204 S sets move.printingAcceleration too (RRF GCodes5.cpp ConfigureAccelerations)");
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.TravelAcceleration).Result, Is.EqualTo(700.0).Within(1e-2),
                        "M204 S sets move.travelAcceleration too (RRF GCodes5.cpp ConfigureAccelerations)");
        });
#pragma warning restore
    }

    /// <summary>
    /// M566 (mm/min) sets the machine jerk limit and pulls the printing jerk with it; M205 (mm/s)
    /// sets only the printing jerk and is clamped to the machine limit
    /// </summary>
    /// <remarks>
    /// RRF Move2.cpp Move::SetInstantDv: includingMax (M566) writes both arrays, otherwise (M205)
    /// printing = min(value, max). move.axes[].jerk and .printingJerk are reported in mm/min
    /// (Move.cpp axes table, InverseConvertSpeedToMmPerMin)
    /// </remarks>
    [Test]
    public async Task M205AndM566SetJerk()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        Axis axis = await bench.Host.ReadModelAsync(model => model.Move.Axes[0]);
        Extruder extruder = await bench.Host.ReadModelAsync(model => model.Move.Extruders[0]);

        await bench.Host.ExecuteCodeAsync("M566 X1200 E300");
        Assert.Multiple(() =>
        {
            Assert.That(axis.Jerk, Is.EqualTo(1200.0).Within(1e-2),
                        "M566 X sets move.axes[0].jerk (mm/min, RRF Move2.cpp SetInstantDv includingMax)");
            Assert.That(axis.PrintingJerk, Is.EqualTo(1200.0).Within(1e-2),
                        "M566 X sets move.axes[0].printingJerk as well (RRF Move2.cpp SetInstantDv includingMax)");
            Assert.That(extruder.Jerk, Is.EqualTo(300.0).Within(1e-2),
                        "M566 E sets move.extruders[0].jerk (mm/min, RRF Move2.cpp SetInstantDv)");
        });

        await bench.Host.ExecuteCodeAsync("M205 X5");
        Assert.Multiple(() =>
        {
            Assert.That(axis.PrintingJerk, Is.EqualTo(300.0).Within(1e-2),
                        "M205 X5 is 5 mm/s, so move.axes[0].printingJerk becomes 300 mm/min (RRF GCodes2.cpp case 205)");
            Assert.That(axis.Jerk, Is.EqualTo(1200.0).Within(1e-2),
                        "M205 leaves the machine limit move.axes[0].jerk alone (RRF Move2.cpp SetInstantDv, not includingMax)");
        });
    }

    /// <summary>
    /// M208 sets the axis limits: two values are min and max, a single value is the max, and a
    /// single value with S1 is the min
    /// </summary>
    /// <remarks>RRF Move2.cpp Move::ConfigureAxisLimits; move.axes[].min/.max in mm</remarks>
    [Test]
    public async Task M208SetsAxisLimits()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        Axis axis0 = await bench.Host.ReadModelAsync(model => model.Move.Axes[0]);
        Axis axis1 = await bench.Host.ReadModelAsync(model => model.Move.Axes[1]);

        await bench.Host.ExecuteCodeAsync("M208 X-5:250");
        Assert.Multiple(() =>
        {
            Assert.That(axis0.Min, Is.EqualTo(-5.0).Within(1e-2),
                        "M208 X-5:250 sets move.axes[0].min (RRF Move2.cpp ConfigureAxisLimits, two values)");
            Assert.That(axis0.Max, Is.EqualTo(250.0).Within(1e-2),
                        "M208 X-5:250 sets move.axes[0].max (RRF Move2.cpp ConfigureAxisLimits, two values)");
        });

        await bench.Host.ExecuteCodeAsync("M208 Y-2 S1");
        await bench.Host.ExecuteCodeAsync("M208 Y240");
        Assert.Multiple(() =>
        {
            Assert.That(axis1.Min, Is.EqualTo(-2.0).Within(1e-2),
                        "M208 Y-2 S1 sets move.axes[1].min (RRF Move2.cpp ConfigureAxisLimits, setMin)");
            Assert.That(axis1.Max, Is.EqualTo(240.0).Within(1e-2),
                        "M208 Y240 sets move.axes[1].max (RRF Move2.cpp ConfigureAxisLimits, single value)");
        });
    }

    /// <summary>M564 S drives move.limitAxes and H drives move.noMovesBeforeHoming</summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 564 writes limitAxes and noMovesBeforeHoming, which the object model
    /// reports as move.limitAxes and move.noMovesBeforeHoming (Move.cpp table). The bench config
    /// runs M564 H0 S0, so both start false
    /// </remarks>
    [Test]
    public async Task M564SetsLimitFlags()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.LimitAxes).Result, Is.False,
                        "the bench config's M564 S0 cleared move.limitAxes");
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.NoMovesBeforeHoming).Result, Is.False,
                        "the bench config's M564 H0 cleared move.noMovesBeforeHoming");
        });

        await bench.Host.ExecuteCodeAsync("M564 S1");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.LimitAxes).Result, Is.True,
                        "M564 S1 sets move.limitAxes (RRF GCodes2.cpp case 564)");
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.NoMovesBeforeHoming).Result, Is.False,
                        "M564 S1 leaves move.noMovesBeforeHoming alone, H was not given (RRF GCodes2.cpp case 564)");
        });

        await bench.Host.ExecuteCodeAsync("M564 H1");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.LimitAxes).Result, Is.True,
                        "M564 H1 leaves move.limitAxes alone, S was not given (RRF GCodes2.cpp case 564)");
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.NoMovesBeforeHoming).Result, Is.True,
                        "M564 H1 sets move.noMovesBeforeHoming (RRF GCodes2.cpp case 564)");
        });

        await bench.Host.ExecuteCodeAsync("M564 S0 H0");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.LimitAxes).Result, Is.False,
                        "M564 S0 clears move.limitAxes (RRF GCodes2.cpp case 564)");
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.NoMovesBeforeHoming).Result, Is.False,
                        "M564 H0 clears move.noMovesBeforeHoming (RRF GCodes2.cpp case 564)");
        });
    }

    /// <summary>
    /// M572 sets the pressure advance. A single S value is the classic coefficient; two values with
    /// L set the second coefficient and its transition
    /// </summary>
    /// <remarks>
    /// RRF Move2.cpp Move::ConfigurePressureAdvance and ExtruderShaper.cpp object model table:
    /// move.extruders[].pressureAdvance reports k0 in seconds, move.extruders[].pressAdv holds k0,
    /// k1 and d. With one S value RRF copies k0 into k1. The pressAdv.k1 and .d fields being kept on
    /// this side is rrf-differences.md section 3
    /// </remarks>
    [Test]
    public async Task M572SetsPressureAdvance()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        Extruder extruder = await bench.Host.ReadModelAsync(model => model.Move.Extruders[0]);

        await bench.Host.ExecuteCodeAsync("M572 D0 S0.05");
        Assert.Multiple(() =>
        {
#pragma warning disable CS0618
            Assert.That(extruder.PressureAdvance, Is.EqualTo(0.05).Within(1e-4),
                        "M572 S sets move.extruders[0].pressureAdvance in seconds (RRF ExtruderShaper GetK0Seconds)");
#pragma warning restore
            Assert.That(extruder.PressAdv.K0, Is.EqualTo(0.05).Within(1e-4),
                        "M572 S sets move.extruders[0].pressAdv.k0 in seconds (RRF ExtruderShaper GetK0Seconds)");
            Assert.That(extruder.PressAdv.K1, Is.EqualTo(0.05).Within(1e-4),
                        "a single M572 S value copies k0 into move.extruders[0].pressAdv.k1 (RRF Move2.cpp ConfigurePressureAdvance)");
        });

        await bench.Host.ExecuteCodeAsync("M572 D0 S0.06:0.08 L2");
        Assert.Multiple(() =>
        {
#pragma warning disable CS0618
            Assert.That(extruder.PressureAdvance, Is.EqualTo(0.06).Within(1e-4),
                        "M572 S sets move.extruders[0].pressureAdvance in seconds (RRF ExtruderShaper GetK0Seconds)");
#pragma warning restore
            Assert.That(extruder.PressAdv.K0, Is.EqualTo(0.06).Within(1e-4),
                        "M572 S0.06:0.08 sets move.extruders[0].pressAdv.k0 (RRF Move2.cpp ConfigurePressureAdvance)");
            Assert.That(extruder.PressAdv.K1, Is.EqualTo(0.08).Within(1e-4),
                        "M572 S0.06:0.08 sets move.extruders[0].pressAdv.k1 (RRF Move2.cpp ConfigurePressureAdvance)");
            Assert.That(extruder.PressAdv.D, Is.EqualTo(2.0).Within(1e-3),
                        "M572 L sets move.extruders[0].pressAdv.d (RRF Move2.cpp ConfigurePressureAdvance dk)");
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
        await using JobBench bench = await JobControlBench.StartAsync();

        string report = (await bench.Host.ExecuteCodeAsync("M584")).Trim();
        Assert.Multiple(() =>
        {
            Assert.That(report, Does.StartWith("Driver assignments:"),
                        "bare M584 reports the driver assignments (RRF GCodes3.cpp DoDriveMapping report branch)");
            Assert.That(report, Does.Contain("X1.0").And.Contain("Y1.1").And.Contain("E1.2"),
                        "the bare M584 report names the drivers the object model holds for X, Y and E");
        });

        // await bench.Host.ExecuteCodeAsync("M569 P1.3 S1");
        await bench.Host.ExecuteCodeAsync("M584 U1.3");
        Axis axisU = await bench.Host.ReadModelAsync(model => model.Move.Axes[2]);
        Extruder extruder = await bench.Host.ReadModelAsync(model => model.Move.Extruders[0]);

        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.Axes.Count).Result, Is.EqualTo(3),
                        "M584 U creates a third axis (RRF GCodes3.cpp DoDriveMapping, new axis branch)");
            Assert.That(axisU.Letter, Is.EqualTo('U'),
                        "the new axis is U (RRF GCodes3.cpp DoDriveMapping)");
            Assert.That(axisU.Drivers[0], Is.EqualTo(new DuetAPI.Utility.DriverId(1, 3)),
                        "M584 U1.3 sets move.axes[2].drivers[0] (RRF GCodes3.cpp DoDriveMapping, SetAxisDriversConfig)");
            Assert.That(axisU.Visible, Is.True,
                        "a new axis is visible by default (RRF GCodes3.cpp DoDriveMapping, numVisibleAxes = numTotalAxes)");
            Assert.That(extruder.Driver, Is.EqualTo(new DuetAPI.Utility.DriverId(1, 2)),
                        "the extruder mapping from config.g is untouched (RRF Move.cpp extruders table, extruderDrivers)");
        });

        string conflict = await bench.Host.ExecuteCodeAsync("M584 V1.0");
        Assert.That(conflict, Does.Contain("1.0").And.Contain("already used"),
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

        await bench.Host.ExecuteCodeAsync("M584 U1.4:1.5");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.Axes[2].Drivers.Count).Result, Is.EqualTo(2));
            Assert.That(axisU.Drivers[0], Is.EqualTo(new DuetAPI.Utility.DriverId(1, 4)));
            Assert.That(axisU.Drivers[1], Is.EqualTo(new DuetAPI.Utility.DriverId(1, 5)));
        });
    }

}
