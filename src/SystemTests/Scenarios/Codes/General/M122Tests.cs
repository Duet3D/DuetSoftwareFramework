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

/// <summary>M122, diagnostics</summary>
[TestFixture]
[GCode("M122")]
public class M122Tests : BenchFixture
{
    /// <summary>
    /// M122 B asks the board for its diagnostics rather than reporting this program's
    /// </summary>
    /// <remarks>
    /// The report is fetched a chunk at a time, each chunk a returnInfo request whose type is
    /// TypeDiagnosticsPart0 and whose param is the chunk number, with the board answering each in a
    /// standard reply. The regression cases are <c>testcases/can/m122-diagnostics-6hc.yaml</c> and
    /// <c>m122-diagnostics-1hcl.yaml</c>, which compare the report's structure rather than its
    /// numbers because almost every value in it is a counter or a temperature
    /// </remarks>
    [Test]
    public async Task M122AsksABoardForItsDiagnostics()
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M122 B{Machines.DriverBoard}");
        Assert.That(reply, Reply.IsNotError, "a board's diagnostics are fetched, not refused");

        (byte board, CanMessageReturnInfo sent) = bench.CanMaster.LastCanMessage<CanMessageReturnInfo>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the request goes to the board B named");
            Assert.That(sent.Type, Is.EqualTo(CanMessageReturnInfo.TypeDiagnosticsPart0),
                        "asking for the diagnostics rather than the firmware version");
        });
    }

    /// <summary>
    /// M122 reports DSF's own diagnostics, the motion engine's among them
    /// </summary>
    [Category("KnownGap")]
    [TestCase("M122")]
    [TestCase("M122 B0")]
    public async Task M122ReportsDiagnostics(string code)
    {
        await using Bench bench = await Bench.StartAsync();
        string reply = await bench.Host.ExecuteCodeAsync(code);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Reply.IsSuccess("=== Diagnostics ==="));
            Assert.That(reply, Reply.IsSuccess("=== Duet Control Server ==="));
            Assert.That(reply, Reply.IsSuccess("=== Move ==="), "M122 contains the Move section (RRF Move::Diagnostics)");
            Assert.That(bench.CanMaster.CanMessages<CanMessageReturnInfo>(), Is.Empty, "board 0 is answered here, not asked for over CAN");
        });
    }
}
