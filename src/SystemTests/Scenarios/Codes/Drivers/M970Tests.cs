using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Drivers;

/// <summary>
/// M970, the step mode and phase stepping, with its feedforward gains (M970.1, M970.2) and per-driver form
/// (M970.3)
/// </summary>
[TestFixture]
[GCode("M970", "M970.1", "M970.2", "M970.3")]
public class M970Tests : BenchFixture
{
    /// <summary>
    /// M970 puts each drive's drivers into a step mode, one message per driver, and records the drive
    /// as phase stepping only once the board has taken it
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m970-phase-stepping-on-and-off.yaml</c> and
    /// <c>m970-phase-stepping-more-axes.yaml</c>. Move::SetStepMode sends to every driver of the
    /// drive and only then sets <c>remotePhaseStepDrives</c> (Move.cpp:2395), so a drive whose board
    /// refused is left as it was rather than reading as phase stepping
    /// </remarks>
    [Test]
    public async Task M970SetsTheStepMode()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync("M970 Y1 Z1")).Trim(), Is.Empty);

        var sent = bench.CanMaster.CanMessages<CanMessageM970>();
        Assert.That(sent.Select(message => (message.Board, message.Message.P, message.Message.S)),
                    Is.EqualTo(new[]
                    {
                        (DriversRig.MainDriverBoard, (byte?)1, (byte?)1),
                        (DriversRig.MainDriverBoard, (byte?)2, (byte?)1)
                    }),
                    "one message per driver, carrying the mode as S");

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[1].PhaseStep), Is.True);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].PhaseStep), Is.False,
                        "X was not named, so it is untouched");
        });

        await bench.Host.ExecuteCodeAsync("M970 Y0 Z0");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[1].PhaseStep), Is.False,
                    "and mode 0 is step/direction again");
    }

    /// <summary>
    /// M970 E reaches the extruder's own board, and a board that declines without saying why is
    /// reported by naming the drive and the mode it was asked for
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m970-phase-stepping-more-axes.yaml</c>, whose E line records
    /// "Could not set step mode for extruder 0 to mode 1" - RepRapFirmware's fallback when the reply
    /// buffer came back empty (GCodes3.cpp:953)
    /// </remarks>
    [Test]
    public async Task M970OnAnExtruderWhoseBoardDeclines()
    {
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.ReportWith<CanMessageM970>(
                string.Empty, CodeResult.ErrorNotSupported)).StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M970 E1");

        (byte board, CanMessageM970 sent) = bench.CanMaster.LastCanMessage<CanMessageM970>();
        Assert.Multiple(async () =>
        {
            Assert.That(board, Is.EqualTo(DriversRig.ClosedLoopBoard));
            Assert.That(sent.P, Is.EqualTo(0));
            Assert.That(reply.TrimEnd(), Does.StartWith("Error:"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].PhaseStep), Is.False,
                        "a refused mode leaves the drive as it was");
        });
    }

    /// <summary>
    /// A board that never answers makes M970 an error carrying the timeout, and leaves the drive as
    /// it was
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m970-phase-stepping-on-and-off.yaml</c>, whose lines record
    /// "Error: M970: CAN response timeout: board 1, req type 6072, RID 12" against a second MB6HC
    /// running RepRapFirmware in expansion mode, which has no case for the message. A timeout is
    /// <c>canResponseTimeout</c> rather than an error reply, so the text is there while the severity
    /// is the caller's: Move::SetStepMode treats it as a failure and M970 returns it
    /// </remarks>
    [Test]
    [Category("LongRunning")]
    public async Task M970ReportsABoardThatNeverAnswers()
    {
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.NeverAnswer<CanMessageM970>())
                                             .StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M970 X1");
        Assert.Multiple(async () =>
        {
            Assert.That(reply.TrimEnd(), Does.StartWith("Error: M970: CAN response timeout: board 1, req type 6072"),
                        "RepRapFirmware's own wording, which CanResponse.FromTimeout already carries");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].PhaseStep), Is.False,
                        "and the axis is still in step/direction mode, because nothing took it out");
        });
    }

    /// <summary>
    /// A mode number that is not a mode is refused before anything reaches a board, in the wording
    /// that belongs to how it was named
    /// </summary>
    /// <remarks>
    /// RepRapFirmware refuses it in two places and two wordings. An axis letter is read through
    /// <c>GetLimitedUIValue(letter, StepMode::unknown)</c>, which reports the parameter
    /// (GCodeBuffer.cpp:624); an extruder is read as an array and checked by ConfigureStepMode
    /// itself, which reports the value (GCodes3.cpp:943). <c>StepMode::unknown</c> is the first value
    /// out of range either way
    /// </remarks>
    [Test]
    public async Task M970RefusesAnUnknownMode()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ExecuteCodeAsync("M970 X2").Result.TrimEnd(),
                        Does.StartWith("Error:"),
                        "an axis names the parameter, because that is what read it");
            Assert.That(bench.Host.ExecuteCodeAsync("M970 E2").Result.TrimEnd(),
                        Does.StartWith("Error:"),
                        "and an extruder names the value, because ConfigureStepMode checked it itself");
            Assert.That(bench.CanMaster.CanMessages<CanMessageM970>(), Is.Empty,
                        "neither reaches a board");
        });
    }

    /// <summary>
    /// A gain that is not a gain is refused with the value written out as RepRapFirmware writes it
    /// </summary>
    /// <remarks>
    /// RRF GCodes3.cpp:971 <c>reply.printf("Invalid K%c %f", ...)</c>, which is six decimal places
    /// like the report beside it
    /// </remarks>
    [Test]
    public async Task M970RefusesANegativeGain()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync("M970.1 X-1")).TrimEnd(),
                    Does.StartWith("Error:"));
    }

    /// <summary>
    /// An axis letter with no number after it is a parse error, not a request to report
    /// </summary>
    /// <remarks><c>testcases/drivers/error-missing-parameters-drivers.yaml</c></remarks>
    [Test]
    public async Task M970RefusesABareAxisLetter()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync("M970 X")).TrimEnd(),
                    Does.StartWith("Error: at column 7: M970: expected number after 'X'"));
    }

    /// <summary>
    /// A bare M970 reports the step mode of every drive
    /// </summary>
    /// <remarks><c>testcases/drivers/m970-report-phase-stepping.yaml</c></remarks>
    [Test]
    public async Task M970ReportsEveryDrivesStepMode()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        await bench.Host.ExecuteCodeAsync("M970 X1");
        Assert.That((await bench.Host.ExecuteCodeAsync("M970")).TrimEnd(),
                    Is.EqualTo("Axis step mode - X:1, Y:0, Z:0, E:0"));
    }

    /// <summary>
    /// M970.1 and M970.2 set the feedforward gains, which are kept on this side and reported from it
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m970-1-velocity-constant.yaml</c> and
    /// <c>m970-2-acceleration-constant.yaml</c>. Move::ConfigurePhaseStepping stores the gain before
    /// forwarding it (Move.cpp:2330), so the report shows the value was taken even while the forward
    /// fails; the gains are sent as V and A of the same M970 message rather than as sub-codes of
    /// their own (CanInterface.cpp SetRemotePhaseStepParam)
    /// </remarks>
    [Test]
    public async Task M970FeedforwardGains()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        await bench.Host.ExecuteCodeAsync("M970.1 X2000.0");
        (_, CanMessageM970 velocity) = bench.CanMaster.LastCanMessage<CanMessageM970>();
        Assert.Multiple(() =>
        {
            Assert.That(velocity.P, Is.EqualTo(0), "the driver of the axis named");
            Assert.That(velocity.V, Is.EqualTo(2000.0f).Within(1e-3), "Kv goes out as V");
            Assert.That(velocity.S, Is.Null, "and no step mode, which this form did not ask for");
        });

        await bench.Host.ExecuteCodeAsync("M970.2 Y60000.0 Z60000.0");
        (_, CanMessageM970 acceleration) = bench.CanMaster.LastCanMessage<CanMessageM970>();
        Assert.That(acceleration.A, Is.EqualTo(60000.0f).Within(1e-2), "and Ka goes out as A");

        Assert.Multiple(async () =>
        {
            Assert.That((await bench.Host.ExecuteCodeAsync("M970.1")).TrimEnd(),
                        Is.EqualTo("Axis phase step Kv - X:2000.000000, Y:0.000000, Z:0.000000, E:0.000000"));
            Assert.That((await bench.Host.ExecuteCodeAsync("M970.2")).TrimEnd(),
                        Is.EqualTo("Axis phase step Ka - X:0.000000, Y:60000.000000, Z:60000.000000, E:0.000000"));
        });
    }

    /// <summary>
    /// M970.3 configures the phase correction of one driver, named by P rather than by an axis
    /// </summary>
    /// <remarks><c>testcases/drivers/m970-3-waveform-correction.yaml</c></remarks>
    [Test]
    public async Task M970Point3AddressesOneDriver()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        const byte board = DriversRig.MainDriverBoard;

        await bench.Host.ExecuteCodeAsync($"M970.3 P{Expansion.Driver(board, 0)} S2 J1.5 O200");

        (byte sentTo, CanMessageM970Point3 sent) = bench.CanMaster.LastCanMessage<CanMessageM970Point3>();
        Assert.Multiple(() =>
        {
            Assert.That(sentTo, Is.EqualTo(board));
            Assert.That(sent.P, Is.EqualTo(0), "P is the driver local to that board");
            Assert.That(sent.S, Is.EqualTo(2), "S is the harmonic");
            Assert.That(sent.J, Is.EqualTo(1.5f).Within(1e-4), "J the magnitude in degrees");
            Assert.That(sent.O, Is.EqualTo(200.0f).Within(1e-3), "O the phase in degrees");
        });
    }

    /// <summary>
    /// M970.3 needs P, because the correction belongs to a driver and there is no whole-machine form
    /// </summary>
    /// <remarks><c>testcases/drivers/error-missing-parameters-drivers.yaml</c></remarks>
    [Test]
    public async Task M970Point3WithoutPIsRefused()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ExecuteCodeAsync("M970.3").Result.TrimEnd(),
                        Is.EqualTo("Error: M970.3: missing parameter 'P'"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageM970Point3>(), Is.Empty);
        });
    }

    /// <summary>
    /// A board that takes a phase stepping value but warns about it has its warning reported
    /// </summary>
    /// <remarks>
    /// CanInterface::SetRemoteDriverStepMode answers with a result code, and M970 is what carries it.
    /// A warning means the drive took the mode, so the object model keeps what the code wrote
    /// </remarks>
    [Test]
    public async Task M970ReportsABoardWarningAboutTheStepMode()
    {
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.ReportWith<CanMessageM970>(
                "phase stepping is untuned on this driver", CodeResult.Warning)).StartAsync();

        string reply = (await bench.Host.ExecuteCodeAsync("M970 E1")).TrimEnd();
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Is.EqualTo("Warning: M970: phase stepping is untuned on this driver"),
                        "the board took the mode and warned about it, and the warning is the code's result");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].PhaseStep), Is.True,
                        "a warning is not a refusal, so the drive keeps the mode the code set");
        });
    }
}
