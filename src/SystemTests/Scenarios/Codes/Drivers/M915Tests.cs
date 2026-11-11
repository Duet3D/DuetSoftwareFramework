using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Drivers;

/// <summary>M915, stall detection</summary>
[TestFixture]
[GCode("M915")]
public class M915Tests : BenchFixture
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
    public async Task M915PConfiguresOneDriver()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);
        const byte board = DriversRig.MainDriverBoard;

        // T, the coolStep register, is the one parameter the references leave alone: the datasheet
        // warns that a wrong value silently reduces motor current, so no case sends it to the rig
        Assert.That((await bench.Host.ExecuteCodeAsync($"M915 P{Expansion.Driver(board, 0)} S5 F1 H300 T100 R1")).Trim(), Is.Empty);

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
    public async Task M915ByAxisLetterAndExtruderNumber()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync("M915 X Y Z S5 F1 H150 R0")).Trim(), Is.Empty);
        Assert.That((await bench.Host.ExecuteCodeAsync("M915 E0 S4 F0 H200 R0")).Trim(), Is.Empty,
                    "an extruder is named by number, which is a drive as much as a letter is");

        var sent = bench.CanMaster.CanMessages<CanMessageM915>();
        Assert.That(sent, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(sent[0].Board, Is.EqualTo(DriversRig.MainDriverBoard));
            Assert.That(sent[0].Message.d, Is.EqualTo(0b111), "X, Y and Z are drivers 0, 1 and 2 there");
            Assert.That(sent[0].Message.S, Is.EqualTo(5));
            Assert.That(sent[1].Board, Is.EqualTo(DriversRig.ClosedLoopBoard));
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
    public async Task M915RefusesABareExtruderLetter()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        string reply = await bench.Host.ExecuteCodeAsync("M915 E");
        Assert.Multiple(() =>
        {
            Assert.That(reply.TrimEnd(), Is.EqualTo("Error: at column 7: M915: expected number after 'E'"));
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
        await using Bench bench = await Bench.With(DriversRig.Config)
                                             .WithController(canMaster => canMaster.ReportWith<CanMessageM915>(
                message => message.S is null ? boardReport : string.Empty)).StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M915 P{Expansion.Driver(DriversRig.MainDriverBoard, 0)}");
        Assert.That(reply.TrimEnd(), Is.EqualTo(boardReport));
    }
}
