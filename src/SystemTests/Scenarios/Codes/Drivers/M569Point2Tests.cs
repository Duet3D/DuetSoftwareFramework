using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Drivers;

/// <summary>
/// M569.2: reading and writing a smart driver register, and the sine table waveform correction added at 3.7
/// </summary>
/// <remarks>
/// RRF CanInterface::ConfigureRemoteDriver case 2 and Duet3Expansion's ProcessM569Point2. The waveform
/// correction is recorded under <c>boards[].drivers[].config.waveformCorrection</c> (rrf-differences.md
/// section 3); a register write is a debugging aid that later driver configuration overwrites, so it is not
/// </remarks>
[TestFixture]
[GCode("M569.2")]
public class M569Point2Tests : BenchFixture
{
    /// <summary>
    /// A register write is acknowledged silently, and a read answers with the value the board replies with
    /// </summary>
    /// <remarks><c>m569-2-write-register.yaml</c> and <c>m569-2-read-register.yaml</c></remarks>
    [Test]
    public async Task M569Point2ReadsAndWritesARegister()
    {
        const string registerReport = "Register 0x6c value 0x14008055";
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.ReportWith<CanMessageM569Point2>(
                message => message.V is null && message.R is not null ? registerReport : string.Empty)).StartAsync();
        const byte board = DriversRig.ClosedLoopBoard;

        Assert.That(await bench.Host.ExecuteCodeAsync($"M569.2 P{Expansion.Driver(board, 0)} R108 V335577173"), Reply.IsEmpty,
                    "a register write is acknowledged silently");
        (byte sentTo, CanMessageM569Point2 written) = bench.CanMaster.LastCanMessage<CanMessageM569Point2>();
        Assert.Multiple(() =>
        {
            Assert.That(sentTo, Is.EqualTo(board));
            Assert.That(written.P, Is.EqualTo(0));
            Assert.That(written.R, Is.EqualTo(108), "R is the register number");
            Assert.That(written.V, Is.EqualTo(335577173), "V is the 32-bit value to write");
            Assert.That(written.S, Is.Null, "and no waveform correction");
        });

        string read = await bench.Host.ExecuteCodeAsync($"M569.2 P{Expansion.Driver(board, 0)} R108");
        (_, CanMessageM569Point2 reading) = bench.CanMaster.LastCanMessage<CanMessageM569Point2>();
        Assert.Multiple(async () =>
        {
            Assert.That(read.TrimEnd(), Is.EqualTo(registerReport), "a read answers with the board's value");
            Assert.That(reading.Generic.ParamMap, Is.EqualTo(0b11u), "and carries P and R alone");
            Assert.That(await bench.Host.ReadModelAsync(model => model.FindBoard(board)!.Drivers![0].Config.WaveformCorrection.Count),
                        Is.Zero, "nothing about a register is recorded");
        });
    }

    /// <summary>
    /// M569.2 with a register value but no register number is refused before the bus
    /// </summary>
    /// <remarks>
    /// <c>error-missing-parameters-drivers-m569.yaml</c>, last line. RRF CanInterface.cpp ConfigureRemoteDriver
    /// requires R whenever V is given, because a value with no register to write it to would otherwise
    /// degenerate into a report
    /// </remarks>
    [Test]
    public async Task M569Point2WithoutRIsRefused()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync($"M569.2 P{DriversRig.MainDriver(0)} V4");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Reply.IsError(CodeErrors.MissingParameter('R')));
            Assert.That(bench.CanMaster.CanMessages(), Is.Empty, "and nothing is sent");
        });
    }

    /// <summary>
    /// M569.2 without P is refused before the bus
    /// </summary>
    /// <remarks>RRF GCodes3.cpp ConfigureDriver <c>gb.MustSee('P')</c></remarks>
    [Test]
    public async Task M569Point2WithoutPIsRefused()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync("M569.2 R108");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Reply.IsError(CodeErrors.MissingParameter('P')));
            Assert.That(bench.CanMaster.CanMessages<CanMessageM569Point2>(), Is.Empty);
        });
    }

    /// <summary>
    /// S, J and O correct one harmonic of the sine table, which is recorded, updated by a later S with only O,
    /// and removed by J0; a bare M569.2 reports what the board has
    /// </summary>
    /// <remarks><c>m569-2-waveform-correction.yaml</c></remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M569Point2CorrectsTheWaveform()
    {
        string report = $"Driver {DriversRig.MainDriver(5)} waveform correction: S4 J1.280 O180.0";
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.ReportWith<CanMessageM569Point2>(
                message => message.S is null && message.R is null ? report : string.Empty)).StartAsync();
        const byte board = DriversRig.MainDriverBoard;
        string driver = DriversRig.MainDriver(5);

        Assert.That(await bench.Host.ExecuteCodeAsync($"M569.2 P{driver} S4 J1.28 O180"), Reply.IsEmpty);
        (byte sentTo, CanMessageM569Point2 correction) = bench.CanMaster.LastCanMessage<CanMessageM569Point2>();
        Assert.Multiple(() =>
        {
            Assert.That(sentTo, Is.EqualTo(board));
            Assert.That(correction.P, Is.EqualTo(5));
            Assert.That(correction.S, Is.EqualTo(4), "S is the harmonic being corrected");
            Assert.That(correction.J, Is.EqualTo(1.28f).Within(1e-4), "J its magnitude in degrees");
            Assert.That(correction.O, Is.EqualTo(180.0f).Within(1e-3), "O its phase in degrees");
            Assert.That(correction.R, Is.Null, "and no register access, which this form did not ask for");
            Assert.That(correction.V, Is.Null);
        });

        DriverWaveformCorrection[] recorded = await ReadCorrectionsAsync(bench, board, 5);
        Assert.That(recorded.Select(item => (item.Harmonic, item.Magnitude, item.Phase)),
                    Is.EqualTo(new[] { (4, 1.28f, 180.0f) }), "the correction is recorded");

        Assert.That((await bench.Host.ExecuteCodeAsync($"M569.2 P{driver}")).TrimEnd(), Is.EqualTo(report),
                    "a bare M569.2 answers with the board's report");

        await bench.Host.ExecuteCodeAsync($"M569.2 P{driver} S8 J2");
        await bench.Host.ExecuteCodeAsync($"M569.2 P{driver} S4 O0");
        recorded = await ReadCorrectionsAsync(bench, board, 5);
        Assert.That(recorded.Select(item => (item.Harmonic, item.Magnitude, item.Phase)),
                    Is.EqualTo(new[] { (4, 1.28f, 0.0f), (8, 2.0f, 0.0f) }),
                    "a second harmonic is added beside the first, and O alone changes only the phase");

        await bench.Host.ExecuteCodeAsync($"M569.2 P{driver} S4 J0");
        recorded = await ReadCorrectionsAsync(bench, board, 5);
        Assert.That(recorded.Select(item => item.Harmonic), Is.EqualTo(new[] { 8 }), "J0 removes the correction of that harmonic");
    }

    /// <summary>
    /// A correction the board refuses comes back as an error and leaves the record as it was
    /// </summary>
    /// <remarks>Duet3Expansion CommandProcessor.cpp ProcessM569Point2 and TmcDriverState::ConfigureLutCorrection</remarks>
    [TestCase("S6 J1", "Only harmonics that are multiples of 4 can be represented in the sine table")]
    [TestCase("S20 J1", "Waveform correction harmonic out of range")]
    [TestCase("S4 J95", "Waveform correction magnitude out of range")]
    [TestCase("S4 J1 O90", "Sine table correction phase must be 0 or 180")]
    [TestCase("S12 O180", "Driver 5 has no waveform correction for harmonic 12")]
    public async Task M569Point2ReportsARefusedCorrection(string parameters, string refusal)
    {
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.ReportWith<CanMessageM569Point2>(refusal, CodeResult.Error))
                                             .StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M569.2 P{DriversRig.MainDriver(5)} {parameters}");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Reply.IsError(refusal));
            Assert.That(await ReadCorrectionsAsync(bench, DriversRig.MainDriverBoard, 5), Is.Empty, "nothing refused is recorded");
        });
    }

    /// <summary>Read the waveform correction recorded for a driver</summary>
    private static Task<DriverWaveformCorrection[]> ReadCorrectionsAsync(Bench bench, byte board, int driver)
        => bench.Host.ReadModelAsync(model => model.FindBoard(board)!.Drivers![driver].Config.WaveformCorrection.ToArray());
}
