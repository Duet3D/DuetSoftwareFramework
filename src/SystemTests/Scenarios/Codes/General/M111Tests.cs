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

/// <summary>M111, the debug flags</summary>
[TestFixture]
[GCode("M111")]
public class M111Tests : BenchFixture
{
    /// <summary>
    /// M111 without parameters reports the debug state as a success message.
    /// </summary>
    /// <remarks>
    /// RRF RepRap::ProcessM111 (RepRap.cpp): with nothing to change it falls through to the debug
    /// report. There is no object model field for debug flags, so the reply is the contract
    /// </remarks>
    [Test]
    public async Task M111ReportsTheDebugState()
    {
        await using Bench bench = await Bench.StartAsync();
        string reply = await bench.Host.ExecuteCodeAsync("M111");
        Assert.That(reply.Trim(), Is.Not.Empty, "M111 without parameters reports the debug state (RRF RepRap::ProcessM111)");
        Assert.That(reply, Does.Not.Contain("Error"), "M111 without parameters is a report, not an error (RRF RepRap::ProcessM111)");
    }

    /// <summary>
    /// M111 B sets the debug flags of an expansion board rather than of this program
    /// </summary>
    /// <remarks>
    /// B only means a board from RRF 3.6.0; before that it was the debug buffer size, so a
    /// regression here would turn a debug request into a buffer allocation. The regression case is
    /// <c>testcases/can/m111-debug-on-expansion.yaml</c>, which uses module 0 because that is the
    /// one module every board has - the higher numbers mean different things in main and expansion
    /// firmware
    /// </remarks>
    [Test]
    public async Task M111SetsDebugFlagsOnAnExpansionBoard()
    {
        await using Bench bench = await Bench.StartAsync();

        await bench.Host.ExecuteCodeAsync($"M111 B{Machines.DriverBoard} P0 S1");

        (byte board, CanMessageM111 sent) = bench.CanMaster.LastCanMessage<CanMessageM111>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the request goes to the board B named");
            Assert.That(sent.P, Is.EqualTo(0), "naming the module");
            Assert.That(sent.S, Is.EqualTo(1), "and that all of its flags are wanted");
        });

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync($"M111 B{Machines.DriverBoard}");
        (_, CanMessageM111 query) = bench.CanMaster.LastCanMessage<CanMessageM111>();
        Assert.Multiple(() =>
        {
            Assert.That(query.P, Is.Null, "a bare M111 B is a query, so it names no module");
            Assert.That(query.S, Is.Null, "and asks for no change");
        });
    }
}
