using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Codes;

/// <summary>
/// M915 and M970 against the rig the regression suite records on: which drivers each addresses, what
/// reaches their boards, and what the object model keeps
/// </summary>
/// <remarks>
/// The bench half of <c>lib/DuetRegressionTesting/testcases/drivers/m915-*.yaml</c> and
/// <c>m970*.yaml</c>. Both address drives rather than drivers and expand a drive to its drivers on
/// this side, which is the part a scenario can pin: M915 puts one request per board on the bus with
/// the board's own driver bitmap in it, and M970 one per driver
/// </remarks>
[TestFixture]
public class StallDetectionAndPhaseStepTests : BenchFixture
{
    /// <summary>
    /// M915 P addresses a driver directly, and the settings are recorded under
    /// boards[].drivers[].config.stallDetection
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m915-stall-detection.yaml</c>. RepRapFirmware forwards the settings and
    /// keeps nothing; the object model entry is rrf-differences.md section 3, which added
    /// <c>stallDetection</c> because stall configuration had no home at all
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M915PConfiguresOneDriver()
    {
        await using JobBench bench = await DriversBench.StartAsync();
        const byte board = DriversBench.MainDriverBoard;

        // T, the coolStep register, is the one parameter the references leave alone: the datasheet
        // warns that a wrong value silently reduces motor current, so no case sends it to the rig
        bench.CanMaster.ClearCapture();
        Assert.That((await bench.Host.ExecuteCodeAsync($"M915 P{board}.0 S5 F1 H300 T100 R1")).Trim(), Is.Empty);

        (byte sentTo, CanMessageM915 sent) = bench.CanMaster.LastCanMessage<CanMessageM915>();
        Assert.Multiple(() =>
        {
            Assert.That(sentTo, Is.EqualTo(board));
            Assert.That(sent.d, Is.EqualTo(1), "the bitmap is the board's own driver numbers, which "
                                               + "only this side can work out");
            Assert.That(sent.S, Is.EqualTo(5), "S is the StallGuard threshold");
            Assert.That(sent.F, Is.EqualTo(1), "F averages over four full steps");
            Assert.That(sent.H, Is.EqualTo(300), "H is the full steps per second below which "
                                                 + "StallGuard is not trusted");
            Assert.That(sent.T, Is.EqualTo(100), "T is the coolStep register value");
            Assert.That(sent.R, Is.EqualTo(1), "R is the action on stall");
        });

        DriverStallDetection stall = await bench.Host.ReadModelAsync(
            model => model.Boards[board].Drivers![0].Config.StallDetection);
        Assert.Multiple(() =>
        {
            Assert.That(stall.Threshold, Is.EqualTo(5));
            Assert.That(stall.Filter, Is.True);
            Assert.That(stall.MinimumSpeed, Is.EqualTo(300));
            Assert.That(stall.CoolStep, Is.EqualTo(100));
            Assert.That(stall.RaiseEvent, Is.True);
        });
    }

    /// <summary>
    /// M915 by axis letter expands each letter to every driver of that axis, and an extruder number
    /// to the extruder's driver, so a machine whose drives sit on two boards gets one request each
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m915-stall-detection-by-axis-letter.yaml</c>: X, Y and Z are on one board
    /// and the extruder on another, so the settings go out as two requests with different bitmaps
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M915ByAxisLetterAndExtruderNumber()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        Assert.That((await bench.Host.ExecuteCodeAsync("M915 X Y Z S5 F1 H150 R0")).Trim(), Is.Empty);
        Assert.That((await bench.Host.ExecuteCodeAsync("M915 E0 S4 F0 H200 R0")).Trim(), Is.Empty,
                    "an extruder is named by number, which is a drive as much as a letter is");

        var sent = bench.CanMaster.CanMessages<CanMessageM915>();
        Assert.That(sent, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(sent[0].Board, Is.EqualTo(DriversBench.MainDriverBoard));
            Assert.That(sent[0].Message.d, Is.EqualTo(0b111), "X, Y and Z are drivers 0, 1 and 2 there");
            Assert.That(sent[0].Message.S, Is.EqualTo(5));
            Assert.That(sent[1].Board, Is.EqualTo(DriversBench.ClosedLoopBoard));
            Assert.That(sent[1].Message.d, Is.EqualTo(1), "and the extruder is driver 0 of its own board");
            Assert.That(sent[1].Message.S, Is.EqualTo(4));
        });
    }

    /// <summary>
    /// A bare E is refused rather than standing for every extruder, quoting where it stood
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/error-missing-parameters-drivers.yaml</c>: Move::ConfigureStallDetection
    /// reads the extruder list with <c>GetUnsignedArray(..., false)</c>, which refuses to supply a
    /// default, so the letter alone is a parse error
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M915RefusesABareExtruderLetter()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        string reply = await bench.Host.ExecuteCodeAsync("M915 E");
        Assert.Multiple(() =>
        {
            Assert.That(reply.TrimEnd(), Is.EqualTo("Error:  at column 7: M915: expected number after 'E'"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageM915>(), Is.Empty,
                        "and nothing is sent, so no driver is configured from a default nobody named");
        });
    }

    /// <summary>
    /// Naming drives with no values is a report, which the boards answer and this side concatenates
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m915-report-stall-settings.yaml</c> and
    /// <c>m915-stall-detection-by-axis-letter.yaml</c>. The derived mm/sec figures in it are the
    /// board's, worked out from the steps per millimetre and microstepping it was last sent, so the
    /// report travels back unchanged
    /// </remarks>
    [Test]
    public async Task M915ReportsFromTheBoards()
    {
        const string boardReport = "Driver 1.0: stall threshold 4, filter on, full steps/sec 250 (50.0 mm/sec), "
                                   + "coolstep threshold 197 (50.0 mm/sec), event on stall: yes";
        await using JobBench bench = await DriversBench.StartAsync(
            prepareController: canMaster => canMaster.ReportWith<CanMessageM915>(
                message => message.S is null ? boardReport : string.Empty));

        string reply = await bench.Host.ExecuteCodeAsync($"M915 P{DriversBench.MainDriverBoard}.0");
        Assert.That(reply.TrimEnd(), Is.EqualTo(boardReport));
    }

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
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        Assert.That((await bench.Host.ExecuteCodeAsync("M970 Y1 Z1")).Trim(), Is.Empty);

        var sent = bench.CanMaster.CanMessages<CanMessageM970>();
        Assert.That(sent.Select(message => (message.Board, message.Message.P, message.Message.S)),
                    Is.EqualTo(new[]
                    {
                        (DriversBench.MainDriverBoard, (byte?)1, (byte?)1),
                        (DriversBench.MainDriverBoard, (byte?)2, (byte?)1)
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
    [Category("KnownGap")]
    public async Task M970OnAnExtruderWhoseBoardDeclines()
    {
        await using JobBench bench = await DriversBench.StartAsync(
            prepareController: canMaster => canMaster.ReportWith<CanMessageM970>(
                string.Empty, CodeResult.ErrorNotSupported));

        bench.CanMaster.ClearCapture();
        string reply = await bench.Host.ExecuteCodeAsync("M970 E1");

        (byte board, CanMessageM970 sent) = bench.CanMaster.LastCanMessage<CanMessageM970>();
        Assert.Multiple(async () =>
        {
            Assert.That(board, Is.EqualTo(DriversBench.ClosedLoopBoard));
            Assert.That(sent.P, Is.EqualTo(0));
            Assert.That(reply.TrimEnd(), Is.EqualTo("Error: M970: Could not set step mode for extruder 0 to mode 1"));
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
    [Category("KnownGap")]
    public async Task M970ReportsABoardThatNeverAnswers()
    {
        await using JobBench bench = await DriversBench.StartAsync(
            prepareController: canMaster => canMaster.NeverAnswer<CanMessageM970>());

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
    [Category("KnownGap")]
    public async Task M970RefusesAnUnknownMode()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ExecuteCodeAsync("M970 X2").Result.TrimEnd(),
                        Is.EqualTo("Error: M970: parameter 'X' too high"),
                        "an axis names the parameter, because that is what read it");
            Assert.That(bench.Host.ExecuteCodeAsync("M970 E2").Result.TrimEnd(),
                        Is.EqualTo("Error: M970: Unknown mode 2"),
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
    [Category("KnownGap")]
    public async Task M970RefusesANegativeGain()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync("M970.1 X-1")).TrimEnd(),
                    Is.EqualTo("Error: M970.1: Invalid Kv -1.000000"));
    }

    /// <summary>
    /// An axis letter with no number after it is a parse error, not a request to report
    /// </summary>
    /// <remarks><c>testcases/drivers/error-missing-parameters-drivers.yaml</c></remarks>
    [Test]
    public async Task M970RefusesABareAxisLetter()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync("M970 X")).TrimEnd(),
                    Is.EqualTo("Error:  at column 7: M970: expected number after 'X'"));
    }

    /// <summary>
    /// A bare M970 reports the step mode of every drive
    /// </summary>
    /// <remarks><c>testcases/drivers/m970-report-phase-stepping.yaml</c></remarks>
    [Test]
    public async Task M970ReportsEveryDrivesStepMode()
    {
        await using JobBench bench = await DriversBench.StartAsync();

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
    [Category("KnownGap")]
    public async Task M970FeedforwardGains()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
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
        await using JobBench bench = await DriversBench.StartAsync();
        const byte board = DriversBench.MainDriverBoard;

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync($"M970.3 P{board}.0 S2 J1.5 O200");

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
        await using JobBench bench = await DriversBench.StartAsync();

        bench.CanMaster.ClearCapture();
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ExecuteCodeAsync("M970.3").Result.TrimEnd(),
                        Is.EqualTo("Error: M970.3: missing parameter 'P'"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageM970Point3>(), Is.Empty);
        });
    }

    /// <summary>
    /// M914 is not a command this firmware has, and is declined as a warning
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m914-voltage-level-translator.yaml</c>. It sets an expansion header's
    /// signal level on the Alligator board, which is a level translator rather than a CAN device, so
    /// no rig can have one. A warning rather than an error is RepRapFirmware's
    /// <c>warningNotSupported</c>, which is what an M-code with no handler at all falls through to
    /// </remarks>
    [Test]
    public async Task M914IsNotSupported()
    {
        await using JobBench bench = await DriversBench.StartAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync("M914")).TrimEnd(),
                    Is.EqualTo("Warning: M914: Command is not supported"));
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
        await using JobBench bench = await DriversBench.StartAsync(
            prepareController: canMaster => canMaster.ReportWith<CanMessageM970>(
                "phase stepping is untuned on this driver", CodeResult.Warning));

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
