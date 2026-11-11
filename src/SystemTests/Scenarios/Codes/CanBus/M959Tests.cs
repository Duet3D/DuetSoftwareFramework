using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.CanBus;

/// <summary>M959, an expansion board's connection timeout</summary>
[TestFixture]
[GCode("M959")]
public class M959Tests : BenchFixture
{
    /// <summary>
    /// M959 B refuses board 0, which is the main board and has no connection timeout to set
    /// </summary>
    /// <remarks>
    /// B is read with GetLimitedUIValue('B', 1, MaxCanAddress + 1), so 0 is below the range rather
    /// than merely absent. The regression case is
    /// <c>testcases/can/error-missing-parameters-can.yaml</c>
    /// </remarks>
    [Test]
    public async Task M959RefusesTheMainBoard()
    {
        await using Bench bench = await Bench.StartAsync();

        Assert.That(await bench.Host.ExecuteCodeAsync("M959 B0"), Does.StartWith("Error:"),
                    "board 0 is below M959's range (GetLimitedUIValue('B', 1, MaxCanAddress + 1))");
    }

    /// <summary>
    /// M959 T sets how long the main board waits before deciding a board has gone away, records it
    /// in boards[].timeout, and reports it back per board and for the whole bus
    /// </summary>
    /// <remarks>
    /// ExpansionManager::ConfigureConnectionTimeout (ExpansionManager.cpp) updates the main board's
    /// own copy and sends the board a setConnectionTimeout. This timeout is what raises the
    /// expansion-timeout and expansion-reconnect events, so getting it wrong changes when the
    /// machine notices a board has dropped off. The regression case is
    /// <c>testcases/can/m959-expansion-board-behaviour.yaml</c>, which is marked known-bad on the
    /// firmware side: no firmware handles the message yet, so the send times out while the readback
    /// still works from the main board's copy
    /// </remarks>
    [Test]
    public async Task M959SetsAndReportsAConnectionTimeout()
    {
        await using Bench bench = await Bench.StartAsync();

        await bench.Host.ExecuteCodeAsync($"M959 B{Machines.DriverBoard} T30");

        (byte board, CanMessageM959 sent) =
            bench.CanMaster.LastCanMessage<CanMessageM959>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the timeout goes to the board it is about");
            Assert.That(sent.T, Is.EqualTo(30), "carrying the value in seconds");
        });

        // boards[] is in discovery order with the address as a field, so it is matched on rather
        // than indexed
        Assert.That(await bench.Host.ReadModelAsync(
                        model => model.Boards.FirstOrDefault(b => b.CanAddress == Machines.DriverBoard)?.Timeout),
                    Is.EqualTo(30),
                    "and boards[].timeout records it on this side, which is where the main board's own "
                    + "copy lives (ExpansionManager.cpp ConfigureConnectionTimeout)");

        string one = await bench.Host.ExecuteCodeAsync($"M959 B{Machines.DriverBoard}");
        string all = await bench.Host.ExecuteCodeAsync("M959");
        Assert.Multiple(() =>
        {
            Assert.That(one, Does.Contain("Board 1 connection timeout 30 seconds"),
                        "M959 B1 reports that board's timeout from this side's copy, so it answers whether "
                        + "or not the board took the message");
            Assert.That(all, Does.Not.StartWith("Error:"),
                        "and a bare M959 enumerates the boards. It lists none here: the bench's controller "
                        + "answers CAN requests but announces no board, so the only entry is the one this "
                        + "code created, which is RepRapFirmware's state == unknown and is filtered out of "
                        + "the listing there too. The regression suite has announced boards to enumerate");
        });
    }
}
