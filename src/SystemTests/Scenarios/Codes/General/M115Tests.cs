using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.General;

/// <summary>M115, the firmware version of the controller and the expansion boards</summary>
[TestFixture]
[GCode("M115")]
public class M115Tests : BenchFixture
{
    /// <summary>
    /// M115 B asks the board for its firmware version
    /// </summary>
    /// <remarks>
    /// One returnInfo of TypeFirmwareVersion, answered by the board in one reply. The regression
    /// cases are <c>testcases/can/m115-firmware-info-6hc.yaml</c> and
    /// <c>m115-firmware-info-1hcl.yaml</c>, kept separate because the two boards run different
    /// binaries and are versioned independently. It is also the cheapest check that B addressing
    /// reaches the right board at all
    /// </remarks>
    [Test]
    public async Task M115AsksABoardForItsFirmwareVersion()
    {
        await using Bench bench = await Bench.StartAsync();

        await bench.Host.ExecuteCodeAsync($"M115 B{Machines.DriverBoard}");

        (byte board, CanMessageReturnInfo sent) = bench.CanMaster.LastCanMessage<CanMessageReturnInfo>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the request goes to the board B named");
            Assert.That(sent.Type, Is.EqualTo(CanMessageReturnInfo.TypeFirmwareVersion),
                        "asking for the firmware version");
            Assert.That(sent.Param, Is.Zero, "and the first part of it");
        });
    }

    /// <summary>
    /// M115 and M115 B0 both describe the controller in RepRapFirmware's format and then DSF on a line
    /// of its own, without asking anything of the CAN bus.
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 115: "FIRMWARE_NAME: %s FIRMWARE_VERSION: %s ELECTRONICS: %s" followed by
    /// " FIRMWARE_DATE: %s", and a B parameter naming this board answers the same as no B. The same
    /// firmware describes itself in the object model as boards[0] (Platform.cpp objectModelTable).
    /// The DSF line is DSF's own and is recorded in rrf-differences.md
    /// </remarks>
    [TestCase("M115")]
    [TestCase("M115 B0")]
    public async Task M115ReportsTheControllerAndDsf(string code)
    {
        await using Bench bench = await Bench.StartAsync();
        bench.CanMaster.InjectControllerBoardInfo("Duet 3 MB6HC", "MB6HC", "DuetCANMaster for Duet 3 MB6HC", "4.0.0-alpha.1", "2026-11-05");
        await bench.Host.WaitForModelAsync(model => model.Boards[0].ShortName, "MB6HC", "the controller's identity");
        (string dsfVersion, string dsfDate) = await bench.Host.ReadModelAsync(model => (model.SBC!.DSF.Version, model.SBC.DSF.BuildDateTime));

        bench.CanMaster.ClearCapture();
        string reply = await bench.Host.ExecuteCodeAsync(code);

        string[] lines = reply.TrimEnd().Split('\n');
        Assert.That(lines, Has.Length.EqualTo(2), "one line for the controller and one for DSF");
        Assert.Multiple(() =>
        {
            Assert.That(lines[0], Is.EqualTo($"DSF_VERSION: {dsfVersion} DSF_DATE: {dsfDate}"),
                        "DSF's line comes from sbc.dsf");
            Assert.That(lines[1], Is.EqualTo("FIRMWARE_NAME: DuetCANMaster for Duet 3 MB6HC FIRMWARE_VERSION: 4.0.0-alpha.1 "
                                             + "ELECTRONICS: Duet 3 MB6HC FIRMWARE_DATE: 2026-11-05"),
                        "the controller's line is RRF's, filled in from boards[0] (RRF GCodes2.cpp case 115)");
            Assert.That(bench.CanMaster.CanMessages<CanMessageReturnInfo>(), Is.Empty,
                        "board 0 is answered here, not asked for over CAN");
        });
    }

    /// <summary>
    /// M115 still reports DSF before the controller has said what it is
    /// </summary>
    /// <remarks>
    /// boards[0] always exists but its strings stay empty until the controller's BoardInfo arrives,
    /// and a reply with gaps where the values go would read each key as the value of the one before.
    /// RRF cannot get here, because its firmware is the program answering
    /// </remarks>
    [Test]
    public async Task M115SaysWhatItDoesNotKnowAboutTheController()
    {
        await using Bench bench = await Bench.StartAsync();
        string dsfVersion = await bench.Host.ReadModelAsync(model => model.SBC!.DSF.Version);

        string reply = await bench.Host.ExecuteCodeAsync("M115");

        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Contain($"DSF_VERSION: {dsfVersion} "));
            Assert.That(reply, Does.Contain("FIRMWARE_NAME: unknown FIRMWARE_VERSION: unknown ELECTRONICS: unknown FIRMWARE_DATE: unknown"));
        });
    }
}
