using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Motion.InputChannels;

namespace SystemTests.Scenarios.Codes.Motion;

/// <summary>G92, setting the position</summary>
[TestFixture]
[GCode("G92")]
public class G92Tests : BenchFixture
{
    /// <summary>
    /// G92 sets the current position without motion: the user position and, with no offsets in
    /// play, the machine position of the named axis take the given value and other axes keep theirs
    /// </summary>
    /// <remarks>RRF GCodes3.cpp SetPositions writes currentUserPosition for the axes mentioned;
    /// Move.cpp reports userPosition and machinePosition</remarks>
    [Test]
    public async Task G92SetsUserAndMachinePosition()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye);

        Axis axis0 = await bench.Host.ReadModelAsync(model => model.Move.Axes[0]);
        Axis axis1 = await bench.Host.ReadModelAsync(model => model.Move.Axes[1]);

        await bench.Host.ExecuteCodeAsync("G1 X10 F6000");
        await bench.Host.ExecuteCodeAsync("M400");
        await bench.Host.ExecuteCodeAsync("G92 X50");

        Assert.Multiple(() =>
        {
            Assert.That(axis0.UserPosition, Is.EqualTo(50.0).Within(1e-3),
                        "G92 X50 sets move.axes[0].userPosition (RRF GCodes3.cpp SetPositions)");
            Assert.That(axis1.UserPosition, Is.EqualTo(0.0).Within(1e-3),
                        "G92 X50 leaves move.axes[1].userPosition alone (RRF GCodes3.cpp SetPositions)");
        });
        await WaitForMachinePositionAsync(bench, 0, 50.0);
        Assert.That(axis0.MachinePosition, Is.EqualTo(50.0).Within(1e-3),
                    "with no offsets G92 X50 sets move.axes[0].machinePosition too (RRF Move.cpp machinePosition)");
    }
}
