using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Codes;

/// <summary>
/// Drive and axis configuration M-codes and the object model fields each one must set. The expected
/// behaviour is RepRapFirmware's (lib/RepRapFirmware), except where
/// src/Documentation/articles/rrf-differences.md documents a deliberate deviation, which is then the
/// behaviour asserted and cited.
/// M569.2 and M569.6 are omitted: M569.2 reads or writes a driver register and its whole result is
/// the register value in the board's reply, and M569.6 runs a closed-loop tuning move whose outcome
/// is judged by the driver; the fake controller answers every request with an empty StandardReply
/// and cannot script either
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

        Assert.That(await bench.Host.EvaluateRawAsync("move.axes[0].homed"), Is.EqualTo("true"),
                    "the bench config's G92 marks X homed");

        await bench.Host.ExecuteCodeAsync("M18 X");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateRawAsync("move.axes[0].homed").Result, Is.EqualTo("false"),
                        "M18 X clears move.axes[0].homed (RRF GCodes2.cpp case 18, SetAxisNotHomed)");
            Assert.That(bench.Host.EvaluateRawAsync("move.axes[1].homed").Result, Is.EqualTo("true"),
                        "M18 X leaves move.axes[1].homed alone, Y was not named");
        });

        await bench.Host.ExecuteCodeAsync("G92 X0");
        Assert.That(await bench.Host.EvaluateRawAsync("move.axes[0].homed"), Is.EqualTo("true"),
                    "G92 X marks X homed again");

        await bench.Host.ExecuteCodeAsync("M84");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateRawAsync("move.axes[0].homed").Result, Is.EqualTo("false"),
                        "bare M84 clears move.axes[0].homed (RRF GCodes.cpp DisableDrives, SetAllAxesNotHomed)");
            Assert.That(bench.Host.EvaluateRawAsync("move.axes[1].homed").Result, Is.EqualTo("false"),
                        "bare M84 clears move.axes[1].homed (RRF GCodes.cpp DisableDrives, SetAllAxesNotHomed)");
        });

        string reply = await bench.Host.ExecuteCodeAsync("M17");
        Assert.Multiple(() =>
        {
            Assert.That(reply.Trim(), Is.Empty, "M17 succeeds silently (RRF GCodes2.cpp case 17)");
            Assert.That(bench.Host.EvaluateRawAsync("move.axes[0].homed").Result, Is.EqualTo("false"),
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
    /// TODO fix scenario
    [Test]
    public async Task M84SSetsIdleTimeoutWithoutDisabling()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        await bench.Host.ExecuteCodeAsync("M84 S45");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateAsync("move.idle.timeout").Result, Is.EqualTo(45.0).Within(1e-3),
                        "M84 S sets move.idle.timeout in seconds (RRF GCodes2.cpp case 84, SetIdleTimeout)");
            Assert.That(bench.Host.EvaluateRawAsync("move.axes[0].homed").Result, Is.EqualTo("true"),
                        "M84 S does not disable the motors, so X stays homed (RRF GCodes2.cpp case 84, seen branch)");
            Assert.That(bench.Host.EvaluateRawAsync("move.axes[1].homed").Result, Is.EqualTo("true"),
                        "M84 S does not disable the motors, so Y stays homed (RRF GCodes2.cpp case 84, seen branch)");
        });
    }

    /// <summary>
    /// M92 sets the steps per mm of axes and extruders, and the S parameter quotes a value at a
    /// different microstepping, scaling it to the microstepping in use
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 92 and Move2.cpp Move::SetDriveStepsPerMm: with a ustepMultiplier the
    /// stored value is value * currentMicrostepping / ustepMultiplier. The bench drives are at the
    /// default x16
    /// </remarks>
    [Test]
    public async Task M92SetsStepsPerMm()
    {
        // TODO test multi driver axes
        await using JobBench bench = await JobControlBench.StartAsync();

        await bench.Host.ExecuteCodeAsync("M92 X123.5 Y96 E410");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateAsync("move.axes[0].stepsPerMm").Result, Is.EqualTo(123.5).Within(1e-3),
                        "M92 X sets move.axes[0].stepsPerMm (RRF GCodes2.cpp case 92)");
            Assert.That(bench.Host.EvaluateAsync("move.axes[1].stepsPerMm").Result, Is.EqualTo(96.0).Within(1e-3),
                        "M92 Y sets move.axes[1].stepsPerMm (RRF GCodes2.cpp case 92)");
            Assert.That(bench.Host.EvaluateAsync("move.extruders[0].stepsPerMm").Result, Is.EqualTo(410.0).Within(1e-3),
                        "M92 E sets move.extruders[0].stepsPerMm (RRF GCodes2.cpp case 92)");
        });

        await bench.Host.ExecuteCodeAsync("M92 X100 S8");
        Assert.That(await bench.Host.EvaluateAsync("move.axes[0].stepsPerMm"), Is.EqualTo(200.0).Within(1e-3),
                    "M92 X100 S8 at x16 microstepping stores 200 steps/mm (RRF Move2.cpp SetDriveStepsPerMm scales by 16/8)");
    }

    /// <summary>The bare M92 report quotes exactly the values the object model holds</summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 92, report branch: "Steps/mm: " then "X: %.3f, " per axis, then "E:"
    /// and " %.3f" values joined with ':'
    /// </remarks>
    [Test]
    public async Task M92ReportMatchesModel()
    {
        // TODO test multi driver axes
        await using JobBench bench = await JobControlBench.StartAsync();

        double x = await bench.Host.EvaluateAsync("move.axes[0].stepsPerMm");
        double y = await bench.Host.EvaluateAsync("move.axes[1].stepsPerMm");
        double e = await bench.Host.EvaluateAsync("move.extruders[0].stepsPerMm");
        string expected = string.Format(CultureInfo.InvariantCulture,
                                        "Steps/mm: X: {0:F3}, Y: {1:F3}, E: {2:F3}", x, y, e);

        string reply = (await bench.Host.ExecuteCodeAsync("M92")).Trim();
        Assert.That(reply, Is.EqualTo(expected),
                    "bare M92 reports the move.axes[]/move.extruders[] stepsPerMm values in RRF's format (GCodes2.cpp case 92)");
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
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateAsync("move.axes[0].acceleration").Result, Is.EqualTo(1250.0).Within(1e-2),
                        "M201 X sets move.axes[0].acceleration (mm/s^2, RRF Move2.cpp SetAcceleration)");
            Assert.That(bench.Host.EvaluateAsync("move.axes[1].acceleration").Result, Is.EqualTo(1100.0).Within(1e-2),
                        "M201 Y sets move.axes[1].acceleration (mm/s^2, RRF Move2.cpp SetAcceleration)");
            Assert.That(bench.Host.EvaluateAsync("move.extruders[0].acceleration").Result, Is.EqualTo(3000.0).Within(1e-2),
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

        double acceleration = await bench.Host.EvaluateAsync("move.axes[0].acceleration");
        await bench.Host.ExecuteCodeAsync("M201.1 X55");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateAsync("move.axes[0].reducedAcceleration").Result, Is.EqualTo(55.0).Within(1e-2),
                        "M201.1 X sets move.axes[0].reducedAcceleration (mm/s^2, RRF GCodes2.cpp case 201 frac 1)");
            Assert.That(bench.Host.EvaluateAsync("move.axes[0].acceleration").Result, Is.EqualTo(acceleration).Within(1e-2),
                        "M201.1 leaves move.axes[0].acceleration alone (RRF Move2.cpp SetAcceleration reduced branch)");
        });

        // TODO possible RRF bug where it allows reducedAcceleration to be greater than normalAcceleration
        await bench.Host.ExecuteCodeAsync("M201.1 X800");
        Assert.That(await bench.Host.EvaluateAsync("move.axes[0].reducedAcceleration"), Is.EqualTo(800).Within(1e-2),
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
            Assert.That(bench.Host.EvaluateAsync("move.axes[0].speed").Result, Is.EqualTo(9000.0).Within(1e-2),
                        "M203 X sets move.axes[0].speed (mm/min, RRF Move.cpp maxFeedrate)");
            Assert.That(bench.Host.EvaluateAsync("move.extruders[0].speed").Result, Is.EqualTo(4200.0).Within(1e-2),
                        "M203 E sets move.extruders[0].speed (mm/min, RRF Move.cpp maxFeedrate)");
        });

        await bench.Host.ExecuteCodeAsync("M203 Y100 S1");
        Assert.That(await bench.Host.EvaluateAsync("move.axes[1].speed"), Is.EqualTo(6000.0).Within(1e-2),
                    "M203 Y100 S1 is 100 mm/s, reported as 6000 mm/min in move.axes[1].speed (RRF GCodes2.cpp case 203 usingMmPerSec)");

        await bench.Host.ExecuteCodeAsync("M203 I10 S1");
        Assert.That(bench.Host.EvaluateAsync("move.minimumMovementSpeed").Result, Is.EqualTo(10).Within(1e-2),
                    "M203 I10 S1 sets the minimum movement speed to 10 mm/s");


        {
            double minSpeed = 300;
            await bench.Host.ExecuteCodeAsync($"M203 X900 Y200 E200 I{minSpeed}");
            Assert.Multiple(() =>
            {
                Assert.That(bench.Host.EvaluateAsync("move.axes[0].speed").Result, Is.EqualTo(900.0).Within(1e-2),
                            "M203 X sets move.axes[0].speed (mm/min, RRF Move.cpp maxFeedrate)");
                Assert.That(bench.Host.EvaluateAsync("move.axes[1].speed").Result, Is.EqualTo(minSpeed).Within(1e-2),
                            "Y max speed is clamped by the min speed");
                Assert.That(bench.Host.EvaluateAsync("move.extruders[0].speed").Result, Is.EqualTo(minSpeed).Within(1e-2),
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
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateAsync("move.printingAcceleration").Result, Is.EqualTo(Move.DefaultPrintingAcceleration).Within(1e-2));
            Assert.That(bench.Host.EvaluateAsync("move.travelAcceleration").Result, Is.EqualTo(Move.DefaultTravelAcceleration).Within(1e-2));
        });

        await bench.Host.ExecuteCodeAsync("M204 P900 T1600");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateAsync("move.printingAcceleration").Result, Is.EqualTo(900.0).Within(1e-2),
                        "M204 P sets move.printingAcceleration (mm/s^2, RRF GCodes5.cpp ConfigureAccelerations)");
            Assert.That(bench.Host.EvaluateAsync("move.travelAcceleration").Result, Is.EqualTo(1600.0).Within(1e-2),
                        "M204 T sets move.travelAcceleration (mm/s^2, RRF GCodes5.cpp ConfigureAccelerations)");
        });

        await bench.Host.ExecuteCodeAsync("M204 S700");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateAsync("move.printingAcceleration").Result, Is.EqualTo(700.0).Within(1e-2),
                        "M204 S sets move.printingAcceleration too (RRF GCodes5.cpp ConfigureAccelerations)");
            Assert.That(bench.Host.EvaluateAsync("move.travelAcceleration").Result, Is.EqualTo(700.0).Within(1e-2),
                        "M204 S sets move.travelAcceleration too (RRF GCodes5.cpp ConfigureAccelerations)");
        });
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

        await bench.Host.ExecuteCodeAsync("M566 X1200 E300");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateAsync("move.axes[0].jerk").Result, Is.EqualTo(1200.0).Within(1e-2),
                        "M566 X sets move.axes[0].jerk (mm/min, RRF Move2.cpp SetInstantDv includingMax)");
            Assert.That(bench.Host.EvaluateAsync("move.axes[0].printingJerk").Result, Is.EqualTo(1200.0).Within(1e-2),
                        "M566 X sets move.axes[0].printingJerk as well (RRF Move2.cpp SetInstantDv includingMax)");
            Assert.That(bench.Host.EvaluateAsync("move.extruders[0].jerk").Result, Is.EqualTo(300.0).Within(1e-2),
                        "M566 E sets move.extruders[0].jerk (mm/min, RRF Move2.cpp SetInstantDv)");
        });

        await bench.Host.ExecuteCodeAsync("M205 X5");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateAsync("move.axes[0].printingJerk").Result, Is.EqualTo(300.0).Within(1e-2),
                        "M205 X5 is 5 mm/s, so move.axes[0].printingJerk becomes 300 mm/min (RRF GCodes2.cpp case 205)");
            Assert.That(bench.Host.EvaluateAsync("move.axes[0].jerk").Result, Is.EqualTo(1200.0).Within(1e-2),
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

        await bench.Host.ExecuteCodeAsync("M208 X-5:250");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateAsync("move.axes[0].min").Result, Is.EqualTo(-5.0).Within(1e-2),
                        "M208 X-5:250 sets move.axes[0].min (RRF Move2.cpp ConfigureAxisLimits, two values)");
            Assert.That(bench.Host.EvaluateAsync("move.axes[0].max").Result, Is.EqualTo(250.0).Within(1e-2),
                        "M208 X-5:250 sets move.axes[0].max (RRF Move2.cpp ConfigureAxisLimits, two values)");
        });

        await bench.Host.ExecuteCodeAsync("M208 Y-2 S1");
        await bench.Host.ExecuteCodeAsync("M208 Y240");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateAsync("move.axes[1].min").Result, Is.EqualTo(-2.0).Within(1e-2),
                        "M208 Y-2 S1 sets move.axes[1].min (RRF Move2.cpp ConfigureAxisLimits, setMin)");
            Assert.That(bench.Host.EvaluateAsync("move.axes[1].max").Result, Is.EqualTo(240.0).Within(1e-2),
                        "M208 Y240 sets move.axes[1].max (RRF Move2.cpp ConfigureAxisLimits, single value)");
        });
    }

    /// <summary>
    /// M350 sets the microstepping, scales the steps per mm to keep the axis calibrated, and marks
    /// a changed axis not homed
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 350 and GCodes.cpp GCodes::ChangeMicrostepping: on success the steps/mm
    /// are re-quoted from the old microstepping (SetDriveStepsPerMm with the old value as the
    /// multiplier) and SetAxisNotHomed runs for an axis. move.axes[].microstepping.value and
    /// .interpolated come from Move::GetMicrostepping/GetMicrostepInterpolation (Move.cpp table).
    /// The bench config gives X 80 steps/mm and E 420 at the default x16
    /// </remarks>
    /// TODO fix scenario
    [Test]
    public async Task M350SetsMicrosteppingAndScalesStepsPerMm()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        await bench.Host.ExecuteCodeAsync("M350 X32 I0");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateAsync("move.axes[0].microstepping.value").Result, Is.EqualTo(32),
                        "M350 X32 sets move.axes[0].microstepping.value (RRF GCodes2.cpp case 350)");
            Assert.That(bench.Host.EvaluateRawAsync("move.axes[0].microstepping.interpolated").Result, Is.EqualTo("false"),
                        "M350 I0 clears move.axes[0].microstepping.interpolated (RRF GCodes2.cpp case 350)");
            Assert.That(bench.Host.EvaluateAsync("move.axes[0].stepsPerMm").Result, Is.EqualTo(160.0).Within(1e-2),
                        "M350 X32 from x16 doubles move.axes[0].stepsPerMm to 160 (RRF GCodes.cpp ChangeMicrostepping)");
            Assert.That(bench.Host.EvaluateRawAsync("move.axes[0].homed").Result, Is.EqualTo("false"),
                        "M350 marks the axis not homed (RRF GCodes2.cpp case 350, SetAxisNotHomed)");
        });

        await bench.Host.ExecuteCodeAsync("M350 E8 I1");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateAsync("move.extruders[0].microstepping.value").Result, Is.EqualTo(8),
                        "M350 E8 sets move.extruders[0].microstepping.value (RRF GCodes2.cpp case 350)");
            Assert.That(bench.Host.EvaluateRawAsync("move.extruders[0].microstepping.interpolated").Result, Is.EqualTo("true"),
                        "M350 I1 sets move.extruders[0].microstepping.interpolated (RRF GCodes2.cpp case 350)");
            Assert.That(bench.Host.EvaluateAsync("move.extruders[0].stepsPerMm").Result, Is.EqualTo(210.0).Within(1e-2),
                        "M350 E8 from x16 halves move.extruders[0].stepsPerMm to 210 (RRF GCodes.cpp ChangeMicrostepping)");
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
            Assert.That(bench.Host.EvaluateRawAsync("move.limitAxes").Result, Is.EqualTo("false"),
                        "the bench config's M564 S0 cleared move.limitAxes");
            Assert.That(bench.Host.EvaluateRawAsync("move.noMovesBeforeHoming").Result, Is.EqualTo("false"),
                        "the bench config's M564 H0 cleared move.noMovesBeforeHoming");
        });

        await bench.Host.ExecuteCodeAsync("M564 S1");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateRawAsync("move.limitAxes").Result, Is.EqualTo("true"),
                        "M564 S1 sets move.limitAxes (RRF GCodes2.cpp case 564)");
            Assert.That(bench.Host.EvaluateRawAsync("move.noMovesBeforeHoming").Result, Is.EqualTo("false"),
                        "M564 S1 leaves move.noMovesBeforeHoming alone, H was not given (RRF GCodes2.cpp case 564)");
        });

        await bench.Host.ExecuteCodeAsync("M564 H1");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateRawAsync("move.limitAxes").Result, Is.EqualTo("true"),
                        "M564 H1 leaves move.limitAxes alone, S was not given (RRF GCodes2.cpp case 564)");
            Assert.That(bench.Host.EvaluateRawAsync("move.noMovesBeforeHoming").Result, Is.EqualTo("true"),
                        "M564 H1 sets move.noMovesBeforeHoming (RRF GCodes2.cpp case 564)");
        });

        await bench.Host.ExecuteCodeAsync("M564 S0 H0");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateRawAsync("move.limitAxes").Result, Is.EqualTo("false"),
                        "M564 S0 clears move.limitAxes (RRF GCodes2.cpp case 564)");
            Assert.That(bench.Host.EvaluateRawAsync("move.noMovesBeforeHoming").Result, Is.EqualTo("false"),
                        "M564 H0 clears move.noMovesBeforeHoming (RRF GCodes2.cpp case 564)");
        });
    }

    /// <summary>
    /// M569 settings are recorded under boards[].drivers[].config so the machine can be recreated
    /// from the object model
    /// </summary>
    /// <remarks>
    /// rrf-differences.md section 3 documents boards[].drivers[].config (direction, mode, timings,
    /// thresholds) as the deliberate home for what M569 configures; RRF itself stores the remote
    /// direction and mode through ExpansionManager (GCodes3.cpp GCodes::ConfigureDriver,
    /// StoreDriverDirection/StoreDriverMode) and forwards the rest without keeping it
    /// </remarks>
    [Test]
    public async Task M569RecordsDriverConfig()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M569 P1.0 S0 D2 T2.5");
        Assert.That(reply, Does.Not.Contain("Error"), "M569 P1.0 S0 D2 T2.5 is accepted");

        bool direction;
        DriverMode? mode;
        float[] stepTiming;
        using (await bench.Host.Model.AccessReadOnlyAsync(CancellationToken.None))
        {
            Board? board = bench.Host.Model.Boards.FirstOrDefault(b => b.CanAddress == 1);
            Assert.That(board, Is.Not.Null, "board 1 is in the object model after config.g's M569 codes");
            Driver driver = board!.Drivers![0];
            direction = driver.Config.Direction;
            mode = driver.Config.Mode;
            stepTiming = [.. driver.Config.StepTiming];
        }

        Assert.Multiple(() =>
        {
            Assert.That(direction, Is.False,
                        "M569 S0 records boards[].drivers[0].config.direction = false (rrf-differences.md section 3; RRF StoreDriverDirection)");
            Assert.That(mode, Is.EqualTo(DriverMode.SpreadCycle),
                        "M569 D2 records boards[].drivers[0].config.mode = spreadCycle (rrf-differences.md section 3; RRF StoreDriverMode)");
            Assert.That(stepTiming, Is.EqualTo(new float[] { 2.5f, 2.5f, 2.5f, 2.5f }),
                        "M569 T with one value records all four step timings (rrf-differences.md section 3)");
        });
    }

    /// <summary>
    /// The M569 minors the bench can carry: each is repackaged for the driver's board and succeeds
    /// against a board that acknowledges it. The fake replies with an empty StandardReply, which is
    /// a healthy board with nothing to say
    /// </summary>
    /// <remarks>
    /// RRF GCodes3.cpp GCodes::ConfigureDriver forwards every sub-code for a remote driver through
    /// CanInterface::ConfigureRemoteDriver; the reply text is the board's. M569.1 configures closed
    /// loop control, M569.4 commands a torque, M569.7 configures the brake port
    /// </remarks>
    [Test]
    public async Task M569MinorsReachTheDriver()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ExecuteCodeAsync("M569.1 P1.0 T2").Result.Trim(), Is.Empty,
                        "M569.1 is forwarded to the driver's board and succeeds on an acknowledging board (RRF GCodes3.cpp ConfigureDriver)");
            Assert.That(bench.Host.ExecuteCodeAsync("M569.4 P1.0 T0.5").Result.Trim(), Is.Empty,
                        "M569.4 is forwarded to the driver's board and succeeds on an acknowledging board (RRF GCodes3.cpp ConfigureDriver)");
            Assert.That(bench.Host.ExecuteCodeAsync("M569.7 P1.0 C\"1.out1\"").Result.Trim(), Is.Empty,
                        "M569.7 is forwarded to the driver's board and succeeds on an acknowledging board (RRF GCodes3.cpp ConfigureDriver)");
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

        await bench.Host.ExecuteCodeAsync("M572 D0 S0.05");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateAsync("move.extruders[0].pressureAdvance").Result, Is.EqualTo(0.05).Within(1e-4),
                        "M572 S sets move.extruders[0].pressureAdvance in seconds (RRF ExtruderShaper GetK0Seconds)");
            Assert.That(bench.Host.EvaluateAsync("move.extruders[0].pressAdv.k0").Result, Is.EqualTo(0.05).Within(1e-4),
                        "M572 S sets move.extruders[0].pressAdv.k0 in seconds (RRF ExtruderShaper GetK0Seconds)");
            Assert.That(bench.Host.EvaluateAsync("move.extruders[0].pressAdv.k1").Result, Is.EqualTo(0.05).Within(1e-4),
                        "a single M572 S value copies k0 into move.extruders[0].pressAdv.k1 (RRF Move2.cpp ConfigurePressureAdvance)");
        });

        await bench.Host.ExecuteCodeAsync("M572 D0 S0.06:0.08 L2");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateAsync("move.extruders[0].pressAdv.k0").Result, Is.EqualTo(0.06).Within(1e-4),
                        "M572 S0.06:0.08 sets move.extruders[0].pressAdv.k0 (RRF Move2.cpp ConfigurePressureAdvance)");
            Assert.That(bench.Host.EvaluateAsync("move.extruders[0].pressAdv.k1").Result, Is.EqualTo(0.08).Within(1e-4),
                        "M572 S0.06:0.08 sets move.extruders[0].pressAdv.k1 (RRF Move2.cpp ConfigurePressureAdvance)");
            Assert.That(bench.Host.EvaluateAsync("move.extruders[0].pressAdv.d").Result, Is.EqualTo(2.0).Within(1e-3),
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
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateAsync("#move.axes").Result, Is.EqualTo(3),
                        "M584 U creates a third axis (RRF GCodes3.cpp DoDriveMapping, new axis branch)");
            Assert.That(bench.Host.EvaluateRawAsync("move.axes[2].letter").Result, Is.EqualTo("U"),
                        "the new axis is U (RRF GCodes3.cpp DoDriveMapping)");
            Assert.That(bench.Host.EvaluateRawAsync("move.axes[2].drivers[0]").Result, Is.EqualTo("1.3"),
                        "M584 U1.3 sets move.axes[2].drivers[0] (RRF GCodes3.cpp DoDriveMapping, SetAxisDriversConfig)");
            Assert.That(bench.Host.EvaluateRawAsync("move.axes[2].visible").Result, Is.EqualTo("true"),
                        "a new axis is visible by default (RRF GCodes3.cpp DoDriveMapping, numVisibleAxes = numTotalAxes)");
            Assert.That(bench.Host.EvaluateRawAsync("move.extruders[0].driver").Result, Is.EqualTo("1.2"),
                        "the extruder mapping from config.g is untouched (RRF Move.cpp extruders table, extruderDrivers)");
        });

        string conflict = await bench.Host.ExecuteCodeAsync("M584 V1.0");
        Assert.That(conflict, Does.Contain("1.0").And.Contain("already used"),
                    "M584 refuses a driver another axis owns, naming it (rrf-differences.md section 3.1)");
        Assert.That(await bench.Host.EvaluateAsync("#move.axes"), Is.EqualTo(3),
                    "the refused mapping creates no axis (rrf-differences.md section 3.1)");

        await bench.Host.ExecuteCodeAsync("M584 U");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateAsync("#move.axes").Result, Is.EqualTo(3),
                        "M584 U keeps the axis in move.axes[], positions and indices do not move (rrf-differences.md section 3.1)");
            Assert.That(bench.Host.EvaluateAsync("#move.axes[2].drivers").Result, Is.EqualTo(0),
                        "M584 U releases the drivers of U (rrf-differences.md section 3.1)");
        });

        await bench.Host.ExecuteCodeAsync("M584 U1.4:1.5");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateAsync("#move.axes[2].drivers").Result, Is.EqualTo(2));
            Assert.That(bench.Host.EvaluateRawAsync("move.axes[2].drivers[0]").Result, Is.EqualTo("1.4"));
            Assert.That(bench.Host.EvaluateRawAsync("move.axes[2].drivers[1]").Result, Is.EqualTo("1.5"));
        });
    }

    /// <summary>M906 sets the motor currents in mA, and I and T set the idle factor and timeout</summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 906: Move::SetMotorCurrent per drive, I is
    /// Move::SetIdleCurrentFactor(value/100) and T is Move::SetIdleTimeout. move.axes[].current is
    /// the 906 value in mA, move.idle.factor is the 0..1 fraction, move.idle.timeout is in seconds
    /// (Move.cpp tables)
    /// </remarks>
    [Test]
    public async Task M906SetsCurrentsAndIdleControl()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        await bench.Host.ExecuteCodeAsync("M906 X850 E900 I40 T25");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateAsync("move.axes[0].current").Result, Is.EqualTo(850),
                        "M906 X sets move.axes[0].current (mA, RRF Move.cpp GetMotorCurrent 906)");
            Assert.That(bench.Host.EvaluateAsync("move.axes[1].current").Result, Is.EqualTo(800),
                        "M906 leaves the unnamed Y axis current alone");
            Assert.That(bench.Host.EvaluateAsync("move.extruders[0].current").Result, Is.EqualTo(900),
                        "M906 E sets move.extruders[0].current (mA, RRF Move.cpp GetMotorCurrent 906)");
            Assert.That(bench.Host.EvaluateAsync("move.idle.factor").Result, Is.EqualTo(0.4).Within(1e-3),
                        "M906 I40 sets move.idle.factor to 0.4 (RRF GCodes2.cpp case 906, SetIdleCurrentFactor/100)");
            Assert.That(bench.Host.EvaluateAsync("move.idle.timeout").Result, Is.EqualTo(25.0).Within(1e-3),
                        "M906 T sets move.idle.timeout in seconds (RRF GCodes2.cpp case 906, SetIdleTimeout)");
        });
    }

    /// <summary>The bare M906 report quotes exactly the values the object model holds</summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 906, report branch: "Motor current (mA) - " then "X:%d, " per axis,
    /// then "E" and ":%d" per extruder, then ", idle factor %d%%, timeout %.1f sec"
    /// </remarks>
    [Test]
    public async Task M906ReportMatchesModel()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        int x = (int)await bench.Host.EvaluateAsync("move.axes[0].current");
        int y = (int)await bench.Host.EvaluateAsync("move.axes[1].current");
        int e = (int)await bench.Host.EvaluateAsync("move.extruders[0].current");
        int factorPercent = (int)Math.Round(await bench.Host.EvaluateAsync("move.idle.factor") * 100.0);
        double timeout = await bench.Host.EvaluateAsync("move.idle.timeout");
        string expected = string.Format(CultureInfo.InvariantCulture,
                                        "Motor current (mA) - X:{0}, Y:{1}, E:{2}, idle factor {3}%, timeout {4:F1} sec",
                                        x, y, e, factorPercent, timeout);

        string reply = (await bench.Host.ExecuteCodeAsync("M906")).Trim();
        Assert.That(reply, Is.EqualTo(expected),
                    "bare M906 reports the currents, idle factor and timeout the object model holds, in RRF's format (GCodes2.cpp case 906)");
    }

    /// <summary>M913 sets the current percentage without touching the configured mA value</summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 913 (shared handler with 906); move.axes[].percentCurrent is
    /// GetMotorCurrent(axis, 913) and move.axes[].current stays the 906 value (Move.cpp axes table)
    /// </remarks>
    [Test]
    public async Task M913SetsPercentCurrent()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        await bench.Host.ExecuteCodeAsync("M913 X50 E75");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateAsync("move.axes[0].percentCurrent").Result, Is.EqualTo(50),
                        "M913 X sets move.axes[0].percentCurrent (RRF Move.cpp GetMotorCurrent 913)");
            Assert.That(bench.Host.EvaluateAsync("move.extruders[0].percentCurrent").Result, Is.EqualTo(75),
                        "M913 E sets move.extruders[0].percentCurrent (RRF Move.cpp GetMotorCurrent 913)");
            Assert.That(bench.Host.EvaluateAsync("move.axes[0].current").Result, Is.EqualTo(800),
                        "M913 leaves move.axes[0].current at the configured mA (RRF GCodes2.cpp case 913)");
        });
    }

    /// <summary>M917 sets the standstill current percentage</summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 917 (shared handler with 906); move.axes[].percentStstCurrent and
    /// move.extruders[].percentStstCurrent are GetMotorCurrent(drive, 917) (Move.cpp tables)
    /// </remarks>
    [Test]
    public async Task M917SetsStandstillPercent()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        await bench.Host.ExecuteCodeAsync("M917 X60 E70");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.EvaluateAsync("move.axes[0].percentStstCurrent").Result, Is.EqualTo(60),
                        "M917 X sets move.axes[0].percentStstCurrent (RRF Move.cpp GetMotorCurrent 917)");
            Assert.That(bench.Host.EvaluateAsync("move.extruders[0].percentStstCurrent").Result, Is.EqualTo(70),
                        "M917 E sets move.extruders[0].percentStstCurrent (RRF Move.cpp GetMotorCurrent 917)");
        });
    }

    /// <summary>
    /// M915 settings are recorded under boards[].drivers[].config.stallDetection, addressed by axis
    /// letter or by driver
    /// </summary>
    /// <remarks>
    /// rrf-differences.md section 3 documents boards[].drivers[].config.stallDetection as the home
    /// M915 had nowhere else; the parameter meanings are RRF's Move::ConfigureStallDetection
    /// (Move.cpp): S stall threshold, F filter, H minimum full steps per second, T coolStep
    /// register, R the action on a stall
    /// </remarks>
    [Test]
    public async Task M915RecordsStallDetectionConfig()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M915 X S5 F1 H400 T100 R1");
        Assert.That(reply, Does.Not.Contain("Error"), "M915 X S5 F1 H400 T100 R1 is accepted");
        await bench.Host.ExecuteCodeAsync("M915 P1.1 S10");

        int thresholdX, minimumSpeedX, coolStepX, thresholdY;
        bool filterX, raiseEventX;
        using (await bench.Host.Model.AccessReadOnlyAsync(CancellationToken.None))
        {
            Board? board = bench.Host.Model.Boards.FirstOrDefault(b => b.CanAddress == 1);
            Assert.That(board, Is.Not.Null, "board 1 is in the object model");
            DriverStallDetection stallX = board!.Drivers![0].Config.StallDetection;
            thresholdX = stallX.Threshold;
            filterX = stallX.Filter;
            minimumSpeedX = stallX.MinimumSpeed;
            coolStepX = stallX.CoolStep;
            raiseEventX = stallX.RaiseEvent;
            thresholdY = board.Drivers[1].Config.StallDetection.Threshold;
        }

        Assert.Multiple(() =>
        {
            Assert.That(thresholdX, Is.EqualTo(5),
                        "M915 S records the stall threshold of X's driver 1.0 (rrf-differences.md section 3; RRF Move.cpp ConfigureStallDetection S)");
            Assert.That(filterX, Is.True,
                        "M915 F1 records the stall filter (rrf-differences.md section 3; RRF Move.cpp ConfigureStallDetection F)");
            Assert.That(minimumSpeedX, Is.EqualTo(400),
                        "M915 H records the minimum full steps per second (rrf-differences.md section 3; RRF Move.cpp ConfigureStallDetection H)");
            Assert.That(coolStepX, Is.EqualTo(100),
                        "M915 T records the coolStep register value (rrf-differences.md section 3; RRF Move.cpp ConfigureStallDetection T)");
            Assert.That(raiseEventX, Is.True,
                        "M915 R1 records that a stall raises an event (rrf-differences.md section 3; RRF Move.cpp ConfigureStallDetection R)");
            Assert.That(thresholdY, Is.EqualTo(10),
                        "M915 P1.1 addresses the driver directly (RRF Move.cpp ConfigureStallDetection P)");
        });
    }

    /// <summary>M970 is refused: every driver is CAN-connected and phase stepping cannot cross the bus</summary>
    /// <remarks>
    /// rrf-differences.md section 1: RRF refuses phase stepping for any remote driver because the
    /// mode drives the coils from the main board (GCodes3.cpp GCodes::ConfigureStepMode via
    /// Move::SetStepMode), and every driver here is remote, so the code answers the refusal outright
    /// </remarks>
    [Test]
    public async Task M970RefusesPhaseStepping()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M970 X2");
        Assert.That(reply, Does.Contain("Phase stepping is not supported on CAN-connected drivers"),
                    "M970 is refused for CAN-connected drivers (rrf-differences.md section 1)");
    }
}
