using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Motion;

/// <summary>
/// X, Y and Z are the axes with those letters, whatever order M584 created them in, and a machine may
/// not have all three
/// </summary>
/// <remarks>
/// <para>
/// RepRapFirmware always has X, Y and Z as its first three axes, so its code can and does treat axis
/// positions 0, 1 and 2 as X, Y and Z. DSF has no default axes: M584 creates each one, in whatever
/// order config.g names them across separate codes, so every part of the motion system finds X, Y and
/// Z by letter instead. Within one M584 the axes are created in letter order, so the out-of-order
/// machines here are each made by more than one M584.
/// </para>
/// <para>
/// Each scenario runs on the usual order as well as the permuted ones, with the same expectations
/// stated by letter, so the usual machine shows the behaviour is unchanged and the others show it
/// does not depend on where the axes are. The drivers are X 1.0, Y 1.1, Z 1.2, U 1.4 and the extruder
/// 1.3 in every order, so a move reaching the wrong driver shows in the packets
/// </para>
/// </remarks>
[TestFixture]
public class AxisLetterOrderTests : BenchFixture
{
    private const byte XDriver = 0, YDriver = 1, ZDriver = 2, UDriver = 4;
    private const float XyuStepsPerMm = 80.0f, ZStepsPerMm = 400.0f;

    /// <summary>The M584 lines of each machine, and the order the axes end up in</summary>
    private static readonly Dictionary<string, string> Creation = new()
    {
        // The usual config.g: one M584, so the axes come out in letter order
        ["XYZU"] = "M584 X1.0 Y1.1 Z1.2 U1.4 E1.3",

        // Y created before X, and Z after both
        ["YXZU"] = "M584 Y1.1\nM584 X1.0\nM584 Z1.2 U1.4 E1.3",

        // Z left until after U, so it is the fourth axis rather than the third
        ["XYUZ"] = "M584 U1.4 X1.0 Y1.1 E1.3\nM584 Z1.2",

        // Z created first, ahead of the axes that have to be homed before it
        ["ZXYU"] = "M584 Z1.2\nM584 X1.0 Y1.1 U1.4 E1.3"
    };

    /// <summary>Everything but the drive mapping, given by letter so it is the same on every machine</summary>
    private const string Configuration = """
        M92 X80 Y80 Z400 U80 E420
        M906 X800 Y800 Z800 U800 E800
        M201 X500 Y500 Z100 U500 E250
        M203 X6000 Y6000 Z600 U6000 E3600
        M566 X900 Y900 Z60 U900 E120
        M208 X0:200 Y0:200 Z0:150 U0:200
        M302 P1
        M564 H0 S0
        M563 P0 D0
        T0
        """;

    /// <summary>The axis orders every scenario runs on</summary>
    private static readonly string[] Orders = ["XYZU", "YXZU", "XYUZ", "ZXYU"];

    /// <summary>
    /// Boot a machine whose axes were created in the given order
    /// </summary>
    /// <param name="order">Which machine, one of <see cref="Creation"/>'s keys</param>
    /// <param name="before">Configuration to run before M584 creates any axis</param>
    /// <param name="after">Configuration to run once the machine is set up, which is where M669 goes</param>
    /// <param name="prepareSd">Populates the rest of the virtual SD card</param>
    private static Task<JobBench> StartAsync(string order, string before = "", string after = "",
                                             Action<VirtualSd>? prepareSd = null)
        => ScheduleMoveBench.StartAsync(
            $"M953\nM569 P1.0 S1\nM569 P1.1 S1\nM569 P1.2 S1\nM569 P1.3 S1\nM569 P1.4 S1\n{before}\n{Creation[order]}\n{Configuration}\n{after}",
            prepareSd);

    /// <summary>Read one axis' value by its letter</summary>
    private static Task<T> ReadAxisAsync<T>(JobBench bench, char letter, Func<Axis, T> read)
        => bench.Host.ReadModelAsync(model => read(model.Move.Axes.First(axis => axis.Letter == letter)));

    [TestCaseSource(nameof(Orders))]
    public async Task EachLetterMovesTheDriverItIsMappedTo(string order)
    {
        await using JobBench bench = await StartAsync(order);
        await bench.Host.ExecuteCodeAsync("G91");

        IReadOnlyList<ScheduledMove> x = await bench.RunMoveAsync("G1 X10 F6000");
        IReadOnlyList<ScheduledMove> y = await bench.RunMoveAsync("G1 Y10 F6000");
        IReadOnlyList<ScheduledMove> z = await bench.RunMoveAsync("G1 Z1 F600");
        IReadOnlyList<ScheduledMove> u = await bench.RunMoveAsync("G1 U5 F6000");

        Assert.Multiple(() =>
        {
            Assert.That(x.DriversMoved(), Is.EqualTo(new[] { (ScheduleMoveBench.DriverBoard, XDriver) }), "X");
            Assert.That(x.Steps(XDriver), Is.EqualTo(10 * XyuStepsPerMm));
            Assert.That(x.Distance(), Is.EqualTo(10.0f).Within(1e-3f));
            Assert.That(y.DriversMoved(), Is.EqualTo(new[] { (ScheduleMoveBench.DriverBoard, YDriver) }), "Y");
            Assert.That(y.Steps(YDriver), Is.EqualTo(10 * XyuStepsPerMm));
            Assert.That(z.DriversMoved(), Is.EqualTo(new[] { (ScheduleMoveBench.DriverBoard, ZDriver) }), "Z");
            Assert.That(z.Steps(ZDriver), Is.EqualTo(1 * ZStepsPerMm));
            Assert.That(u.DriversMoved(), Is.EqualTo(new[] { (ScheduleMoveBench.DriverBoard, UDriver) }), "U");
            Assert.That(u.Steps(UDriver), Is.EqualTo(5 * XyuStepsPerMm));
        });
    }

    [TestCaseSource(nameof(Orders))]
    public async Task G92SetsThePositionOfTheAxisWithEachLetter(string order)
    {
        await using JobBench bench = await StartAsync(order);

        await bench.Host.ExecuteCodeAsync("G92 X5 Y7 Z3 U2");

        // The machine position is published from the live position, so it is waited for
        foreach ((char letter, float position) in new[] { ('X', 5.0f), ('Y', 7.0f), ('Z', 3.0f), ('U', 2.0f) })
        {
            await bench.Host.WaitForModelAsync(model => model.Move.Axes.First(axis => axis.Letter == letter).MachinePosition,
                                               (float?)position, $"{letter}'s machine position");
        }
        Assert.Multiple(async () =>
        {
            Assert.That(await ReadAxisAsync(bench, 'X', axis => axis.UserPosition), Is.EqualTo(5.0f).Within(1e-3f));
            Assert.That(await ReadAxisAsync(bench, 'Z', axis => axis.UserPosition), Is.EqualTo(3.0f).Within(1e-3f));
            Assert.That(await ReadAxisAsync(bench, 'Z', axis => axis.Homed), Is.True, "G92 marks the named axes homed");
        });

        // A move from there goes from there: 10 more mm of X is 400 steps to X's driver only
        IReadOnlyList<ScheduledMove> move = await bench.RunMoveAsync("G1 X10 F6000");
        Assert.Multiple(() =>
        {
            Assert.That(move.DriversMoved(), Is.EqualTo(new[] { (ScheduleMoveBench.DriverBoard, XDriver) }));
            Assert.That(move.Steps(XDriver), Is.EqualTo(5 * XyuStepsPerMm));
        });
    }

    [TestCaseSource(nameof(Orders))]
    public async Task AToolsDefaultMapsAreTheAxesWithThoseLetters(string order)
    {
        await using JobBench bench = await StartAsync(order);

        int[][] maps = await bench.Host.ReadModelAsync(model => model.Tools[0]!.Axes.Select(map => map.ToArray()).ToArray());
        Assert.That(maps, Is.EqualTo(new[] { new[] { order.IndexOf('X') }, new[] { order.IndexOf('Y') }, new[] { order.IndexOf('Z') } }),
                    "M563 maps X to the axis lettered X, and so on");

        // The tool's offsets are taken off the axes they belong to
        await bench.Host.ExecuteCodeAsync("G10 P0 X2 Y-1 Z0.5");
        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("G1 X10 Y10 Z5 F600");

        Assert.Multiple(() =>
        {
            Assert.That(moves.DriversMoved(), Is.EquivalentTo(new[]
            {
                (ScheduleMoveBench.DriverBoard, XDriver), (ScheduleMoveBench.DriverBoard, YDriver),
                (ScheduleMoveBench.DriverBoard, ZDriver)
            }), "U does not move");
            Assert.That(moves.Steps(XDriver), Is.EqualTo((10 - 2) * XyuStepsPerMm), "X less X's offset");
            Assert.That(moves.Steps(YDriver), Is.EqualTo((10 + 1) * XyuStepsPerMm), "Y less Y's offset");
            Assert.That(moves.Steps(ZDriver), Is.EqualTo((5 - 0.5f) * ZStepsPerMm), "Z less Z's offset");
        });

        Assert.Multiple(async () =>
        {
            Assert.That(await ReadAxisAsync(bench, 'X', axis => axis.UserPosition), Is.EqualTo(10.0f).Within(1e-3f),
                        "the user position comes back to the axis it was commanded on");
            Assert.That(await ReadAxisAsync(bench, 'Z', axis => axis.UserPosition), Is.EqualTo(5.0f).Within(1e-3f));
        });
    }

    [TestCaseSource(nameof(Orders))]
    public async Task M290SBabystepsTheAxisLetteredZ(string order)
    {
        await using JobBench bench = await StartAsync(order);

        string reply = await bench.Host.ExecuteCodeAsync("M290 S0.1");
        Assert.Multiple(async () =>
        {
            Assert.That(reply.Trim(), Is.Empty);
            Assert.That(await ReadAxisAsync(bench, 'Z', axis => axis.Babystep), Is.EqualTo(0.1f).Within(1e-5f));
            foreach (char other in "XYU")
            {
                Assert.That(await ReadAxisAsync(bench, other, axis => axis.Babystep), Is.Zero, $"{other} is not babystepped");
            }
        });

        // The next move carries the babystep, on Z's driver
        await bench.Host.ExecuteCodeAsync("G91");
        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("G1 Z1 F600");
        Assert.Multiple(() =>
        {
            Assert.That(moves.DriversMoved(), Is.EqualTo(new[] { (ScheduleMoveBench.DriverBoard, ZDriver) }));
            Assert.That(moves.Steps(ZDriver), Is.EqualTo((1 + 0.1f) * ZStepsPerMm).Within(1));
        });
    }

    [TestCaseSource(nameof(Orders))]
    public async Task ZHomedWithAProbeWaitsForXAndYWhereverItIs(string order)
    {
        // No Z endstop, so Z homes with the probe and X and Y have to be homed first. Each macro
        // records that it ran
        static void Macros(VirtualSd sd)
        {
            foreach (char letter in "xyz")
            {
                sd.WriteSys($"home{letter}.g", $"G92 {char.ToUpperInvariant(letter)}0\nset global.order = global.order ^ \"{letter}\"\n");
            }
        }
        await using JobBench bench = await StartAsync(order, after: "global order = \"\"", prepareSd: Macros);

        string refused = await bench.Host.ExecuteCodeAsync("G28 Z");
        string xy = new([.. order.Where(letter => letter is 'X' or 'Y')]);
        Assert.That(refused.Trim(), Is.EqualTo($"Error: G28: Must home axes [{xy}] before homing [Z]"),
                    "Z alone cannot be homed before X and Y");

        string reply = await bench.Host.ExecuteCodeAsync("G28 Z Y X");
        string ran = (await bench.Host.ExecuteCodeAsync("echo global.order")).Trim();

        Assert.Multiple(async () =>
        {
            Assert.That(reply.Trim(), Is.Empty);
            Assert.That(ran.Last(), Is.EqualTo('z'), "Z is homed after X and Y, wherever it is in the axis list");
            Assert.That(ran.Order(), Is.EqualTo("xyz"), "each macro ran once");
            Assert.That(await ReadAxisAsync(bench, 'Z', axis => axis.Homed), Is.True);
        });
    }

    [TestCaseSource(nameof(Orders))]
    public async Task CoreXYCouplesTheAxesLetteredXAndY(string order)
    {
        await using JobBench bench = await StartAsync(order, after: "M669 K1");
        await bench.Host.ExecuteCodeAsync("G91");

        IReadOnlyList<ScheduledMove> alongX = await bench.RunMoveAsync("G1 X10 F6000");
        IReadOnlyList<ScheduledMove> alongY = await bench.RunMoveAsync("G1 Y10 F6000");
        IReadOnlyList<ScheduledMove> alongZ = await bench.RunMoveAsync("G1 Z1 F600");

        Assert.Multiple(() =>
        {
            Assert.That(alongX.Steps(XDriver), Is.EqualTo(10 * XyuStepsPerMm), "X is the sum of the two motors");
            Assert.That(alongX.Steps(YDriver), Is.EqualTo(10 * XyuStepsPerMm));
            Assert.That(alongY.Steps(XDriver), Is.EqualTo(10 * XyuStepsPerMm), "Y is their difference");
            Assert.That(alongY.Steps(YDriver), Is.EqualTo(-10 * XyuStepsPerMm));
            Assert.That(alongZ.DriversMoved(), Is.EqualTo(new[] { (ScheduleMoveBench.DriverBoard, ZDriver) }),
                        "Z keeps a motor of its own");
            Assert.That(alongX.Concat(alongY).SelectMany(move => move.Drivers).Select(driver => driver.DriverNumber),
                        Has.None.EqualTo(UDriver).And.None.EqualTo(ZDriver), "nor do Z and U join in");
        });

        int x = order.IndexOf('X'), y = order.IndexOf('Y');
        float[][] inverse = await bench.Host.ReadModelAsync(
            model => ((CoreKinematics)model.Move.Kinematics).InverseMatrix.Select(row => row.ToArray()).ToArray());
        Assert.Multiple(() =>
        {
            Assert.That(new[] { inverse[x][x], inverse[x][y], inverse[y][x], inverse[y][y] }, Is.EqualTo(new[] { 1.0f, 1.0f, 1.0f, -1.0f }),
                        "move.kinematics.inverseMatrix has the coupling at the axes' own indices");
        });
    }

    [TestCaseSource(nameof(Orders))]
    public async Task AMatrixRowGivenByLetterGoesToThatAxis(string order)
    {
        // U's row turns U's own motor twice as far. The columns are motors in axis order, which is the
        // one thing about M669 that is positional, so the row is written for this machine's order
        string row = string.Join(':', order.Select(letter => letter == 'U' ? "2" : "0"));
        await using JobBench bench = await StartAsync(order, after: $"M669 U{row}");
        await bench.Host.ExecuteCodeAsync("G91");

        IReadOnlyList<ScheduledMove> u = await bench.RunMoveAsync("G1 U5 F6000");
        IReadOnlyList<ScheduledMove> z = await bench.RunMoveAsync("G1 Z1 F600");

        Assert.Multiple(() =>
        {
            Assert.That(u.DriversMoved(), Is.EqualTo(new[] { (ScheduleMoveBench.DriverBoard, UDriver) }));
            Assert.That(u.Steps(UDriver), Is.EqualTo(2 * 5 * XyuStepsPerMm), "U's row was replaced");
            Assert.That(z.DriversMoved(), Is.EqualTo(new[] { (ScheduleMoveBench.DriverBoard, ZDriver) }));
            Assert.That(z.Steps(ZDriver), Is.EqualTo(1 * ZStepsPerMm), "Z's row was not");
        });
    }

    [TestCaseSource(nameof(Orders))]
    public async Task ADeltaMovesTheSameWhicheverOrderItsTowersWereCreatedIn(string order)
    {
        // The move is compared driver by driver against the usual order
        const string delta = "M665 L215 R105.6 H240 B80";
        const string moves = "G90\nG92 X0 Y0 Z100";

        await using JobBench canonical = await StartAsync("XYZU", after: delta);
        await using JobBench permuted = await StartAsync(order, after: delta);
        foreach (JobBench bench in new[] { canonical, permuted })
        {
            foreach (string line in moves.Split('\n'))
            {
                await bench.Host.ExecuteCodeAsync(line);
            }
        }

        IReadOnlyList<ScheduledMove> expected = await canonical.RunMoveAsync("G1 X10 Y-5 Z90 F3000");
        IReadOnlyList<ScheduledMove> actual = await permuted.RunMoveAsync("G1 X10 Y-5 Z90 F3000");

        Assert.Multiple(() =>
        {
            Assert.That(expected.DriversMoved(), Is.EquivalentTo(new[]
            {
                (ScheduleMoveBench.DriverBoard, XDriver), (ScheduleMoveBench.DriverBoard, YDriver),
                (ScheduleMoveBench.DriverBoard, ZDriver)
            }), "every tower moves for a move in X, Y and Z");
            Assert.That(actual.DriversMoved(), Is.EquivalentTo(expected.DriversMoved()));
            foreach (byte driver in new[] { XDriver, YDriver, ZDriver, UDriver })
            {
                Assert.That(actual.Steps(driver), Is.EqualTo(expected.Steps(driver)), $"driver 1.{driver}");
            }
            Assert.That(actual.Distance(), Is.EqualTo(expected.Distance()).Within(1e-3f));
            Assert.That(actual.Select(move => move.TotalClocks), Is.EqualTo(expected.Select(move => move.TotalClocks)),
                        "and with the same profile, piece by piece");
        });
    }

    [Test]
    public async Task M669RefusesAMatrixItCannotInvert()
    {
        // Both rows turn the same two motors the same way, so no motor positions tell X from Y. The
        // axes exist, so the matrix can be judged as M669 gives it
        await using JobBench bench = await StartAsync("XYZU");

        string refused = await bench.Host.ExecuteCodeAsync("M669 X1:1 Y1:1");
        await bench.Host.ExecuteCodeAsync("G91");
        IReadOnlyList<ScheduledMove> move = await bench.RunMoveAsync("G1 X10 F6000");

        float[][] inverse = await bench.Host.ReadModelAsync(
            model => ((CoreKinematics)model.Move.Kinematics).InverseMatrix.Select(row => row.ToArray()).ToArray());
        Assert.Multiple(() =>
        {
            Assert.That(refused.Trim(), Is.EqualTo("Error: M669: Invalid kinematics matrix"), "RepRapFirmware's words");
            Assert.That(inverse[0][..2], Is.EqualTo(new[] { 1.0f, 0.0f }), "the geometry that was there stays");
            Assert.That(inverse[1][..2], Is.EqualTo(new[] { 0.0f, 1.0f }));
            Assert.That(move.DriversMoved(), Is.EqualTo(new[] { (ScheduleMoveBench.DriverBoard, XDriver) }), "and still moves");
        });
    }

    [TestCase("M669 A1:0:0:0")]
    [TestCase("M669 X1:1:0:0 A1:0:0:0")]
    public async Task M669RefusesARowForAnAxisTheMachineDoesNotHave(string code)
    {
        // config.g creates the axes before M669 describes them, so a row for a letter with no axis is
        // a mistake rather than something to keep for later. Nothing of the code is applied
        await using JobBench bench = await StartAsync("XYZU");

        string refused = await bench.Host.ExecuteCodeAsync(code);
        await bench.Host.ExecuteCodeAsync("G91");
        IReadOnlyList<ScheduledMove> move = await bench.RunMoveAsync("G1 X10 F6000");

        float[][] inverse = await bench.Host.ReadModelAsync(
            model => ((CoreKinematics)model.Move.Kinematics).InverseMatrix.Select(row => row.ToArray()).ToArray());
        Assert.Multiple(() =>
        {
            Assert.That(refused.Trim(), Is.EqualTo("Error: M669: The machine has no A axis"));
            Assert.That(inverse[0][..4], Is.EqualTo(new[] { 1.0f, 0.0f, 0.0f, 0.0f }), "X's row is as it was");
            Assert.That(move.DriversMoved(), Is.EqualTo(new[] { (ScheduleMoveBench.DriverBoard, XDriver) }), "and X still moves alone");
        });
    }

    [Test]
    public async Task M669RefusesARowLongerThanTheMachineHasAxes()
    {
        // The values are the motors of the machine's axes in axis order, so there are at most four here
        await using JobBench bench = await StartAsync("XYZU");

        string refused = await bench.Host.ExecuteCodeAsync("M669 X1:0:0:0:1");

        Assert.That(refused.Trim(), Is.EqualTo("Error: at column 15: M669: array too long for parameter 'X'"),
                    "RepRapFirmware's words, pointing at the value there is no motor for");
    }

    // ---- Geometries that need more than X, Y and Z ----------------------------------------------

    /// <summary>X, Y, Z, U and V on drivers 1.0, 1.1, 1.2, 1.4 and 1.5, created in the order given</summary>
    private static Task<JobBench> StartCoreXyuMachineAsync(string creation, string after)
        => ScheduleMoveBench.StartAsync($"""
            M953
            M569 P1.0 S1
            M569 P1.1 S1
            M569 P1.2 S1
            M569 P1.4 S1
            M569 P1.5 S1
            {creation}
            M92 X80 Y80 Z400 U80 V80 A80
            M201 X500 Y500 Z100 U500 V500 A500
            M203 X6000 Y6000 Z600 U6000 V6000 A6000
            M564 H0 S0
            {after}
            """);

    private const byte VDriver = 5;

    [Test]
    public async Task M669RefusesAGeometryWhoseAxesTheMachineDoesNotHave()
    {
        // CoreXYU needs U and V. A machine whose fourth axis is A has neither: RepRapFirmware would
        // take A for U because it is the fourth axis, DSF goes by the letter
        await using JobBench bench = await StartCoreXyuMachineAsync("M584 X1.0 Y1.1 Z1.2 A1.4", "G92 X0 Y0 Z0 A0");

        string refused = await bench.Host.ExecuteCodeAsync("M669 K5");
        await bench.Host.ExecuteCodeAsync("G91");
        IReadOnlyList<ScheduledMove> alongX = await bench.RunMoveAsync("G1 X10 F6000");
        IReadOnlyList<ScheduledMove> alongA = await bench.RunMoveAsync("G1 A5 F6000");

        Assert.Multiple(async () =>
        {
            Assert.That(refused.Trim(), Is.EqualTo("Error: M669: The machine has no U or V axis, which coreXYU kinematics needs"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Kinematics.Name), Is.EqualTo(KinematicsName.Cartesian),
                        "the geometry that was there stays");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes.Select(axis => axis.Homed).ToArray()), Has.All.True,
                        "and the axes G92 homed are still homed, as they would not be had the geometry changed");
            Assert.That(alongX.DriversMoved(), Is.EqualTo(new[] { (ScheduleMoveBench.DriverBoard, XDriver) }), "X still moves alone");
            Assert.That(alongX.Steps(XDriver), Is.EqualTo(10 * XyuStepsPerMm));
            Assert.That(alongA.DriversMoved(), Is.EqualTo(new[] { (ScheduleMoveBench.DriverBoard, UDriver) }), "as does A, on 1.4");
            Assert.That(alongA.Steps(UDriver), Is.EqualTo(5 * XyuStepsPerMm));
        });
    }

    [TestCase("M584 X1.0 Y1.1 Z1.2 U1.4 V1.5", "XYZUV")]
    [TestCase("M584 Y1.1\nM584 U1.4 V1.5\nM584 X1.0 Z1.2", "YUVXZ")]
    public async Task CoreXYUDrivesTheMotorsOfItsLettersWhereverTheAxesAre(string creation, string order)
    {
        // RepRapFirmware's CoreXYU matrix: X turns the X and Y motors together, Y turns them apart and
        // the U and V motors apart, U turns the U and V motors together, and Z has a motor of its own
        await using JobBench bench = await StartCoreXyuMachineAsync(creation, "M669 K5");
        Assert.That(await bench.Host.ReadModelAsync(model => new string([.. model.Move.Axes.Select(axis => axis.Letter)])),
                    Is.EqualTo(order), "the axes were created in this order");
        await bench.Host.ExecuteCodeAsync("G91");

        IReadOnlyList<ScheduledMove> alongX = await bench.RunMoveAsync("G1 X10 F6000");
        IReadOnlyList<ScheduledMove> alongY = await bench.RunMoveAsync("G1 Y10 F6000");
        IReadOnlyList<ScheduledMove> alongU = await bench.RunMoveAsync("G1 U5 F6000");
        IReadOnlyList<ScheduledMove> alongZ = await bench.RunMoveAsync("G1 Z1 F600");

        Assert.Multiple(() =>
        {
            Assert.That(alongX.DriversMoved(), Is.EquivalentTo(new[]
            {
                (ScheduleMoveBench.DriverBoard, XDriver), (ScheduleMoveBench.DriverBoard, YDriver)
            }), "X");
            Assert.That(alongX.Steps(XDriver), Is.EqualTo(10 * XyuStepsPerMm));
            Assert.That(alongX.Steps(YDriver), Is.EqualTo(10 * XyuStepsPerMm));
            Assert.That(alongX.Distance(), Is.EqualTo(10.0f).Within(1e-3f));

            Assert.That(alongY.DriversMoved(), Is.EquivalentTo(new[]
            {
                (ScheduleMoveBench.DriverBoard, XDriver), (ScheduleMoveBench.DriverBoard, YDriver),
                (ScheduleMoveBench.DriverBoard, UDriver), (ScheduleMoveBench.DriverBoard, VDriver)
            }), "Y");
            Assert.That(alongY.Steps(XDriver), Is.EqualTo(10 * XyuStepsPerMm));
            Assert.That(alongY.Steps(YDriver), Is.EqualTo(-10 * XyuStepsPerMm));
            Assert.That(alongY.Steps(UDriver), Is.EqualTo(10 * XyuStepsPerMm));
            Assert.That(alongY.Steps(VDriver), Is.EqualTo(-10 * XyuStepsPerMm));
            Assert.That(alongY.Distance(), Is.EqualTo(10.0f).Within(1e-3f));

            Assert.That(alongU.DriversMoved(), Is.EquivalentTo(new[]
            {
                (ScheduleMoveBench.DriverBoard, UDriver), (ScheduleMoveBench.DriverBoard, VDriver)
            }), "U");
            Assert.That(alongU.Steps(UDriver), Is.EqualTo(5 * XyuStepsPerMm));
            Assert.That(alongU.Steps(VDriver), Is.EqualTo(5 * XyuStepsPerMm));
            Assert.That(alongU.Distance(), Is.EqualTo(5.0f).Within(1e-3f));

            Assert.That(alongZ.DriversMoved(), Is.EqualTo(new[] { (ScheduleMoveBench.DriverBoard, ZDriver) }), "Z");
            Assert.That(alongZ.Steps(ZDriver), Is.EqualTo(1 * ZStepsPerMm));

            foreach (ScheduledMove move in alongX.Concat(alongY).Concat(alongU).Concat(alongZ))
            {
                foreach (ScheduleMoveDriver driver in move.Drivers)
                {
                    Assert.That(driver.BoardAddress, Is.EqualTo(ScheduleMoveBench.DriverBoard));
                    Assert.That(driver.IsExtruder, Is.Zero, "every one of them is an axis driver");
                    Assert.That(driver.Extrusion, Is.Zero);
                    ScheduleMoveBench.AssertWatchesNothing(driver, "a travel move stops on nothing");
                }
            }
        });
    }

    // ---- A machine without Z --------------------------------------------------------------------

    /// <summary>X, Y and an extruder, and nothing lettered Z</summary>
    private const string NoZConfig = """
        M953
        M569 P1.0 S1
        M569 P1.1 S1
        M569 P1.3 S1
        M584 X1.0 Y1.1 E1.3
        M92 X80 Y80 E420
        M201 X500 Y500 E250
        M203 X6000 Y6000 E3600
        M208 X0:200 Y0:200
        M302 P1
        M564 H0 S0
        """;

    [Test]
    public async Task WithoutZAToolHasNoZMap()
    {
        await using JobBench bench = await ScheduleMoveBench.StartAsync(NoZConfig + "\nM563 P0 D0\nT0");

        int[][] maps = await bench.Host.ReadModelAsync(model => model.Tools[0]!.Axes.Select(map => map.ToArray()).ToArray());
        Assert.That(maps, Is.EqualTo(new[] { new[] { 0 }, new[] { 1 }, Array.Empty<int>() }),
                    "X and Y map to their axes and Z to nothing");

        // And the tool still moves X and Y
        await bench.Host.ExecuteCodeAsync("G91");
        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("G1 X10 Y10 F6000");
        Assert.Multiple(() =>
        {
            Assert.That(moves.Steps(XDriver), Is.EqualTo(10 * XyuStepsPerMm));
            Assert.That(moves.Steps(YDriver), Is.EqualTo(10 * XyuStepsPerMm));
        });
    }

    [Test]
    public async Task WithoutZM290SIsAnError()
    {
        await using JobBench bench = await ScheduleMoveBench.StartAsync(NoZConfig);

        string refused = await bench.Host.ExecuteCodeAsync("M290 S0.1");
        string accepted = await bench.Host.ExecuteCodeAsync("M290 X0.1");

        Assert.Multiple(async () =>
        {
            Assert.That(refused.Trim(), Is.EqualTo("Error: M290: The machine has no Z axis"));
            Assert.That(accepted.Trim(), Is.Empty, "the axes that do exist can still be babystepped");
            Assert.That(await ReadAxisAsync(bench, 'X', axis => axis.Babystep), Is.EqualTo(0.1f).Within(1e-5f));
            Assert.That(await ReadAxisAsync(bench, 'Y', axis => axis.Babystep), Is.Zero, "the refused code changed nothing");
        });
    }

    [Test]
    public async Task WithoutZACartesianMachineStillMoves()
    {
        await using JobBench bench = await ScheduleMoveBench.StartAsync(NoZConfig);
        await bench.Host.ExecuteCodeAsync("G91");

        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("G1 X10 F6000");

        Assert.That(moves.DriversMoved(), Is.EqualTo(new[] { (ScheduleMoveBench.DriverBoard, XDriver) }));
    }

    [TestCase("M665 L215 R105.6 H240 B80", "The machine has no Z axis, which delta kinematics needs")]
    [TestCase("M669 K10", "The machine has no Z axis, which Rotary delta kinematics needs")]
    [TestCase("M669 K3", "The machine has no Z axis, which delta kinematics needs")]
    public async Task WithoutZAGeometryThatNeedsItIsRefused(string geometry, string error)
    {
        await using JobBench bench = await ScheduleMoveBench.StartAsync(NoZConfig);

        string refused = await bench.Host.ExecuteCodeAsync(geometry);
        await bench.Host.ExecuteCodeAsync("G91");
        IReadOnlyList<ScheduledMove> move = await bench.RunMoveAsync("G1 X10 F6000");

        Assert.Multiple(async () =>
        {
            Assert.That(refused.Trim(), Is.EqualTo($"Error: {geometry[..4]}: {error}"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Kinematics.Name), Is.EqualTo(KinematicsName.Cartesian),
                        "the geometry that was there stays");
            Assert.That(move.DriversMoved(), Is.EqualTo(new[] { (ScheduleMoveBench.DriverBoard, XDriver) }), "and moves as it did");
            Assert.That(move.Steps(XDriver), Is.EqualTo(10 * XyuStepsPerMm));
        });
    }

    [Test]
    public async Task WithoutZAScaraStillMovesItsArms()
    {
        // A pen plotter: a SCARA needs only its two joints, and its crosstalk onto Z has nothing to act
        // on. The move is compared driver by driver against the same SCARA with a Z axis. The distal
        // joint is limited to one side, so the elbow can bend only one way: which way an arm free to
        // bend either way ends up is state, which the two benches need not have arrived at alike
        const string scara = "M669 K4 B0:135";
        const string start = "G90\nG92 X100 Y50";

        await using JobBench withZ = await ScheduleMoveBench.StartAsync(NoZConfig + "\nM569 P1.2 S1\nM584 Z1.2\n" + scara);
        await using JobBench withoutZ = await ScheduleMoveBench.StartAsync(NoZConfig + "\n" + scara);
        foreach (JobBench bench in new[] { withZ, withoutZ })
        {
            foreach (string line in start.Split('\n'))
            {
                await bench.Host.ExecuteCodeAsync(line);
            }
        }

        IReadOnlyList<ScheduledMove> expected = await withZ.RunMoveAsync("G1 X120 Y30 F3000");
        IReadOnlyList<ScheduledMove> actual = await withoutZ.RunMoveAsync("G1 X120 Y30 F3000");

        Assert.Multiple(async () =>
        {
            Assert.That(expected.DriversMoved(), Is.EquivalentTo(new[]
            {
                (ScheduleMoveBench.DriverBoard, XDriver), (ScheduleMoveBench.DriverBoard, YDriver)
            }), "both joints turn for a move in X and Y, and nothing drags Z");
            Assert.That(actual.DriversMoved(), Is.EquivalentTo(expected.DriversMoved()));
            Assert.That(actual.Steps(XDriver), Is.Not.Zero.And.EqualTo(expected.Steps(XDriver)), "the proximal joint");
            Assert.That(actual.Steps(YDriver), Is.Not.Zero.And.EqualTo(expected.Steps(YDriver)), "the distal joint");
            Assert.That(actual.Distance(), Is.EqualTo(expected.Distance()).Within(1e-3f));
            Assert.That(actual, Has.Count.EqualTo(expected.Count), "segmented the same way");
            for (int i = 0; i < Math.Min(actual.Count, expected.Count); i++)
            {
                Assert.That(actual[i].TotalClocks, Is.EqualTo(expected[i].TotalClocks), $"segment {i}'s duration");
                foreach (byte driver in new[] { XDriver, YDriver })
                {
                    Assert.That(actual[i].DriverFor(ScheduleMoveBench.DriverBoard, driver),
                                Is.EqualTo(expected[i].DriverFor(ScheduleMoveBench.DriverBoard, driver)),
                                $"segment {i}, driver 1.{driver}");
                }
            }
            Assert.That(await ReadAxisAsync(withoutZ, 'X', axis => axis.UserPosition), Is.EqualTo(120.0f).Within(1e-2f));
            Assert.That(await ReadAxisAsync(withoutZ, 'Y', axis => axis.UserPosition), Is.EqualTo(30.0f).Within(1e-2f));
        });
    }
}
