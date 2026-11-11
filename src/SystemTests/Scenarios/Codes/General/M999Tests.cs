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

/// <summary>M999, restarting an expansion board</summary>
[TestFixture]
[GCode("M999")]
public class M999Tests : BenchFixture
{
    /// <summary>
    /// M999 B restarts an expansion board and expects no reply from it
    /// </summary>
    /// <remarks>
    /// The board goes away without answering, so what a regression would look like is the main board
    /// starting to expect a reply - which would turn every board restart into a spurious timeout
    /// error. The regression cases are <c>testcases/can/m999-restart-expansion-6hc.yaml</c> and
    /// <c>m999-restart-expansion-1hcl.yaml</c>, which list the code under <c>no_reply</c> for that
    /// reason and dwell fifteen seconds afterwards so the board is back before the next case runs
    /// </remarks>
    [Test]
    public async Task M999RestartsAnExpansionBoard()
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M999 B{Machines.DriverBoard}");
        Assert.That(reply, Does.Not.StartWith("Error:"), "restarting a board is not an error");

        (byte board, _) = bench.CanMaster.LastCanMessage<CanMessageReset>();
        Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the reset goes to the board B named");
    }
}
