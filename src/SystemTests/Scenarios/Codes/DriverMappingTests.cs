using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Codes;

/// <summary>
/// M92, M350 and M584 against the rig the regression suite records on: the steps per millimetre and
/// microstepping one message carries for every drive at once, and the mapping that decides which
/// drivers a drive has
/// </summary>
/// <remarks>
/// The bench half of <c>lib/DuetRegressionTesting/testcases/drivers/m92-*.yaml</c>,
/// <c>m350-*.yaml</c> and <c>m584-*.yaml</c>. Unlike the currents, these are gathered across every
/// drive the code names and sent in one request per board - RepRapFirmware accumulates them into one
/// <c>CanDriversData</c> and sends it after the loop (Move2.cpp
/// Move::UpdateRemoteStepsPerMmAndMicrostepping)
/// </remarks>
[TestFixture]
public class DriverMappingTests : BenchFixture
{
    /// <summary>
    /// M92 sets the steps per millimetre of every axis it names, in one request carrying all of them
    /// </summary>
    /// <remarks><c>testcases/drivers/m92-set-steps-per-mm.yaml</c></remarks>
    [Test]
    public async Task M92SetsStepsPerMm()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        Assert.That((await bench.Host.ExecuteCodeAsync("M92 X100 Y100 Z800")).Trim(), Is.Empty);

        var sent = bench.CanMaster.CanPayloads<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>();
        Assert.That(sent, Has.Count.EqualTo(1), "three axes on one board are one request");
        Assert.That(sent[0].Payload.Length,
                    Is.EqualTo(new CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping { DriversToUpdate = 0b111 }.GetActualDataLength()),
                    "carrying three drives' values and stopping there");

        (byte board, CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping message) =
            bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(DriversBench.MainDriverBoard));
            Assert.That(message.DriversToUpdate, Is.EqualTo(0b111), "drivers 0, 1 and 2 of that board");
            Assert.That(message.Values[0].GetStepsPerUnit(), Is.EqualTo(100.0f).Within(1e-3));
            Assert.That(message.Values[1].GetStepsPerUnit(), Is.EqualTo(100.0f).Within(1e-3));
            Assert.That(message.Values[2].GetStepsPerUnit(), Is.EqualTo(800.0f).Within(1e-3));
        });

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].StepsPerMm), Is.EqualTo(100.0f).Within(1e-3));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[1].StepsPerMm), Is.EqualTo(100.0f).Within(1e-3));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[2].StepsPerMm), Is.EqualTo(800.0f).Within(1e-3));
        });
    }

    /// <summary>
    /// M92 S quotes the values at one microstepping and the firmware rescales them to the one in use
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m92-steps-at-microstepping.yaml</c>: at x32, values quoted at x16 are
    /// stored doubled
    /// </remarks>
    [Test]
    public async Task M92SRescalesToTheMicrosteppingInUse()
    {
        await using JobBench bench = await DriversBench.StartAsync(configExtra: "M350 X32 Y32 Z32 I1");

        await bench.Host.ExecuteCodeAsync("M92 X80 Y80 Z400 S16");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].StepsPerMm), Is.EqualTo(160.0f).Within(1e-3));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[1].StepsPerMm), Is.EqualTo(160.0f).Within(1e-3));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[2].StepsPerMm), Is.EqualTo(800.0f).Within(1e-3));
        });

        Assert.That((await bench.Host.ExecuteCodeAsync("M92")).TrimEnd(),
                    Is.EqualTo("Steps/mm: X: 160.000, Y: 160.000, Z: 800.000, E: 420.000"),
                    "and the report quotes what was stored (m92-report-steps-per-mm.yaml)");
    }

    /// <summary>
    /// M92 refuses a value of zero, quoting the column it stood in
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/error-missing-parameters-drivers.yaml</c>: steps per millimetre is read
    /// with GetPositiveFValue, so zero is refused rather than stored and used to plan every later move
    /// </remarks>
    [Test]
    public async Task M92RefusesZeroStepsPerMm()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        string reply = await bench.Host.ExecuteCodeAsync("M92 X0");
        Assert.Multiple(() =>
        {
            Assert.That(reply.TrimEnd(), Is.EqualTo("Error:  at column 6: M92: value must be greater than zero"),
                        "refused where it stood, as RRF's GCodeException quotes a column");
            Assert.That(bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>(),
                        Is.Empty, "and nothing reaches the drivers");
        });
    }

    /// <summary>
    /// A value refused part-way through leaves the drives read before it stored, and tells no board
    /// </summary>
    /// <remarks>
    /// RepRapFirmware stores each drive as it reads it and collects the drives to forward, sending
    /// the one remote update after the whole line has been read (GCodes2.cpp case 92, where
    /// UpdateRemoteStepsPerMmAndMicrostepping is reached only past the loop). <c>M92 X200 Y0</c>
    /// therefore leaves X at 200 in the object model with the boards still stepping it at 80, which
    /// is the firmware's own outcome rather than a choice made here
    /// </remarks>
    [Test]
    public async Task M92KeepsWhatItReadWhenAValueIsRefused()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        string reply = await bench.Host.ExecuteCodeAsync("M92 X200 Y0");
        Assert.Multiple(async () =>
        {
            Assert.That(reply.TrimEnd(), Is.EqualTo("Error:  at column 11: M92: value must be greater than zero"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].StepsPerMm), Is.EqualTo(200.0f).Within(1e-3),
                        "X was read before the refusal, so it stands");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[1].StepsPerMm), Is.EqualTo(80.0f).Within(1e-3),
                        "and Y keeps what it had, because its value was never usable");
        });

        Assert.That(bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>(), Is.Empty,
                    "the boards are told once the whole line has been read, so a refusal on it sends nothing "
                    + "(RRF GCodes2.cpp case 92 reaches UpdateRemoteStepsPerMmAndMicrostepping only after the loop)");
    }

    /// <summary>
    /// M906 refuses an idle time-out of zero the same way
    /// </summary>
    /// <remarks><c>testcases/drivers/error-missing-parameters-drivers.yaml</c></remarks>
    [Test]
    public async Task M906RefusesAZeroIdleTimeout()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        float before = await bench.Host.ReadModelAsync(model => model.Move.Idle.Timeout);
        string reply = await bench.Host.ExecuteCodeAsync("M906 T0");
        Assert.Multiple(async () =>
        {
            Assert.That(reply.TrimEnd(), Is.EqualTo("Error:  at column 7: M906: value must be greater than zero"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Idle.Timeout), Is.EqualTo(before),
                        "and move.idle.timeout keeps the value it had");
        });
    }

    /// <summary>
    /// M350 sets the microstepping, carries the interpolation flag in the message, and rescales the
    /// steps per millimetre so the axis stays calibrated
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m350-microstepping-interpolated.yaml</c>,
    /// <c>m350-microstepping-no-interpolation.yaml</c> and <c>m350-microstepping-extremes.yaml</c>.
    /// Bit 15 of the microstepping field is the interpolation flag, which is what distinguishes the I
    /// parameter reaching the driver from the firmware accepting it and dropping it
    /// </remarks>
    [TestCase(1, false)]
    [TestCase(16, true)]
    [TestCase(256, true)]
    public async Task M350SetsMicrosteppingAndInterpolation(int microstepping, bool interpolated)
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        Assert.That((await bench.Host.ExecuteCodeAsync(
                        $"M350 X{microstepping} Y{microstepping} Z{microstepping} I{(interpolated ? 1 : 0)}")).Trim(),
                    Is.Empty);

        (_, CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping sent) =
            bench.CanMaster.LastCanMessage<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>();
        Assert.Multiple(() =>
        {
            Assert.That(sent.Values[0].GetMicrostepping() & 0x03FF, Is.EqualTo(microstepping),
                        "the microstepping is in bits 0-9");
            Assert.That((sent.Values[0].GetMicrostepping() & 0x8000) != 0, Is.EqualTo(interpolated),
                        "and bit 15 asks the driver to interpolate");
            Assert.That(sent.Values[0].GetStepsPerUnit(), Is.EqualTo(80.0f * microstepping / 16).Within(1e-3),
                        "with the steps per millimetre rescaled from the microstepping in force "
                        + "(RRF GCodes.cpp ChangeMicrostepping)");
        });

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Microstepping.Value),
                        Is.EqualTo(microstepping));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Microstepping.Interpolated),
                        Is.EqualTo(interpolated));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Homed), Is.False,
                        "and a drive whose microstepping changed is no longer known to be where it "
                        + "says it is (RRF GCodes2.cpp case 350, SetAxisNotHomed)");
        });
    }

    /// <summary>
    /// M350 E reaches the extruder's own board, which is a different board from the axes'
    /// </summary>
    /// <remarks><c>testcases/drivers/m350-microstepping-for-extruder.yaml</c></remarks>
    [Test]
    public async Task M350SetsExtruderMicrostepping()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync("M350 E64 I1");

        var sent = bench.CanMaster.CanPayloads<CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping>();
        Assert.That(sent, Has.Count.EqualTo(1), "only the extruder was named");
        Assert.Multiple(() =>
        {
            Assert.That(sent[0].Board, Is.EqualTo(DriversBench.ClosedLoopBoard));
            Assert.That(sent[0].Payload.Length,
                        Is.EqualTo(new CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping { DriversToUpdate = 0b1 }.GetActualDataLength()));
        });

        await bench.Host.ExecuteCodeAsync("M350 E16 I0");
        Assert.That((await bench.Host.ExecuteCodeAsync("M350")).TrimEnd(),
                    Is.EqualTo("Microstepping - X:16(on), Y:16(on), Z:16(on), E:16"),
                    "the report suffixes (on) for an interpolating drive and nothing for one that is "
                    + "not (m350-microstepping-for-extruder.yaml)");
    }

    /// <summary>
    /// A bare M350 reports every drive, and a machine that never mentioned M350 reports x16
    /// interpolated, which is the state RepRapFirmware starts every drive in
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m350-report-microstepping.yaml</c>. Move::Init runs
    /// <c>SetDriverMicrostepping(drive, 16, true)</c> for every drive before config.g
    /// </remarks>
    [Test]
    public async Task M350ReportsTheStateEveryDriveStartsIn()
    {
        await using JobBench bench = await DriversBench.StartAsync(withDriveSettings: false);

        Assert.That((await bench.Host.ExecuteCodeAsync("M350")).TrimEnd(),
                    Is.EqualTo("Microstepping - X:16(on), Y:16(on), Z:16(on), E:16(on)"));
    }

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
    [Category("KnownGap")]
    public async Task M584MapsAxesAndResetsTheStepMode()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        Assert.That((await bench.Host.ExecuteCodeAsync("M584 X1.3 Y1.4 Z1.5")).Trim(), Is.Empty);

        var stepModes = bench.CanMaster.CanMessages<CanMessageM970>();
        Assert.That(stepModes.Select(message => (message.Board, message.Message.P, message.Message.S)),
                    Is.EqualTo(new[]
                    {
                        (DriversBench.MainDriverBoard, (byte?)3, (byte?)0),
                        (DriversBench.MainDriverBoard, (byte?)4, (byte?)0),
                        (DriversBench.MainDriverBoard, (byte?)5, (byte?)0)
                    }),
                    "one step mode message per driver assigned, each asking for step/direction");

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Drivers[0]),
                        Is.EqualTo(new DriverId(DriversBench.MainDriverBoard, 3)));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[2].Drivers[0]),
                        Is.EqualTo(new DriverId(DriversBench.MainDriverBoard, 5)));
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
    /// done, and undoing it would leave the machine in neither state
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M584ReportsAStepModeResetThatWentUnanswered()
    {
        await using JobBench bench = await DriversBench.StartAsync(
            prepareController: canMaster => canMaster.NeverAnswer<CanMessageM970>());

        string reply = await bench.Host.ExecuteCodeAsync("M584 X1.3 Y1.4");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.Not.StartWith("Error"), "the mapping stands");
            Assert.That(reply.TrimEnd().Split('\n'), Has.Length.EqualTo(2),
                        "one timeout line per drive, because a drive stops at the first driver that "
                        + "did not answer");
            Assert.That(reply, Does.Contain("CAN response timeout: board 1, req type 6072"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Drivers[0]),
                        Is.EqualTo(new DriverId(DriversBench.MainDriverBoard, 3)),
                        "and the drivers are the ones M584 named");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[1].Drivers[0]),
                        Is.EqualTo(new DriverId(DriversBench.MainDriverBoard, 4)),
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
        await using JobBench bench = await DriversBench.StartAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync("M584")).TrimEnd(),
                    Is.EqualTo("Driver assignments: X1.0 Y1.1 Z1.2 E2.0, 3 axes visible"));
    }

    /// <summary>
    /// M584 P hides the axes past the count it is given, without taking their drivers away
    /// </summary>
    /// <remarks><c>testcases/drivers/m584-hide-axis.yaml</c></remarks>
    [Test]
    public async Task M584PHidesAxes()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        await bench.Host.ExecuteCodeAsync("M584 P2");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[2].Visible), Is.False,
                        "P2 leaves X and Y visible and hides Z");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[2].Drivers.Count), Is.EqualTo(1),
                        "a hidden axis keeps its drivers");
        });

        Assert.That((await bench.Host.ExecuteCodeAsync("M584")).TrimEnd(),
                    Is.EqualTo("Driver assignments: X1.0 Y1.1 Z1.2 E2.0, 2 axes visible"));
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
    [Category("KnownGap")]
    public async Task M584CreatesAnAxisWithTheDefaultsADriveStartsWith()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        await bench.Host.ExecuteCodeAsync("M584 U1.3");
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
        });

        await bench.Host.ExecuteCodeAsync("M584 V1.4 R1 S1");
        Assert.That((await bench.Host.ExecuteCodeAsync("M584")).TrimEnd(),
                    Is.EqualTo("Driver assignments: X1.0 Y1.1 Z1.2 U1.3 (r)(c)V1.4 E2.0, 5 axes visible"),
                    "the report marks a rotational axis (r) and a continuous one (c) before its "
                    + "drivers (RRF GCodes3.cpp DoDriveMapping report branch)");
    }

    /// <summary>
    /// One axis can be driven by two drivers, and both get the axis' settings
    /// </summary>
    /// <remarks><c>testcases/drivers/m584-multiple-drivers-per-axis.yaml</c></remarks>
    [Test]
    public async Task M584GivesAnAxisTwoDrivers()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync("M584 Z1.2:1.3");

        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[2].Drivers.ToArray()),
                    Is.EqualTo(new[] { new DriverId(DriversBench.MainDriverBoard, 2), new DriverId(DriversBench.MainDriverBoard, 3) }));

        await bench.Host.ExecuteCodeAsync("M906 Z400");
        var currents = bench.CanMaster.CanMessages<CanMessageMultipleDrivesRequestMotorCurrents>();
        Assert.That(currents[^1].Message.DriversToUpdate, Is.EqualTo(0b1100),
                    "the axis' current reaches both of its drivers in one request, because it is one "
                    + "drive (RRF Move2.cpp SetMotorCurrent iterates the drive's drivers)");

        Assert.That((await bench.Host.ExecuteCodeAsync("M584")).TrimEnd(),
                    Is.EqualTo("Driver assignments: X1.0 Y1.1 Z1.2:1.3 E2.0, 3 axes visible"),
                    "and the report joins them with a colon");
    }
}
