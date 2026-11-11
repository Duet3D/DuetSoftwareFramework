using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Motion;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Probes.ProbeSetup;

namespace SystemTests.Scenarios.Codes.Probes;

/// <summary>G28, homing</summary>
[TestFixture]
[GCode("G28")]
public class G28Tests : BenchFixture
{
    /// <summary>
    /// G28 X runs homex.g; the G1 H1 move stopped by its endstop homes the axis and sets it to the
    /// endstop's coordinate, which for a low-end endstop is the axis minimum
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes::DoHome (GCodes.cpp) runs the homing macro; the
    /// waitingForSpecialMoveToComplete state (GCodes4.cpp) intersects the axes being homed with
    /// ms.endstopsTriggered, sets each such axis to AxisMinimum or AxisMaximum by its endstop's
    /// position and calls SetAxisIsHomed, so move.axes[0].homed becomes true and machinePosition the
    /// axis minimum. Which axes triggered comes from the latched stop reports, not from re-reading
    /// the switch: rrf-differences.md section 2
    /// </remarks>
    [Test]
    public async Task G28HomesAxisWhenEndstopStopsTheMove()
    {
        await using Bench bench = await StartBenchAsync(XEndstopLine,
            sd => sd.WriteSys("homex.g", "G91\nG1 H1 X-150 F3000\nG90\n"));

        Task<string> home = bench.Host.ExecuteCodeAsync("G28 X");
        (uint MoveId, (byte Board, byte Driver)[] Drivers) move =
            await WaitForArmedMoveAsync(bench.CanMaster, 0, "the G1 H1 move arriving armed on the endstop");
        Assert.That(move.Drivers, Is.EquivalentTo(new[] { ((byte)1, (byte)0) }),
                    "the homing move arms driver 1.0, which is X");
        StopMove(bench.CanMaster, move);

        string reply = await home;
        Assert.That(reply.Trim(), Is.Empty, "G28 X completed without error");
        bool homed = await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Homed);
        double position = await bench.Host.ReadModelAsync(model => model.Move.Axes[0].MachinePosition!.Value);
        Assert.Multiple(() =>
        {
            Assert.That(homed, Is.True,
                        "G28 X sets move.axes[0].homed after the endstop stop (RRF GCodes4.cpp SetAxisIsHomed)");
            Assert.That(position, Is.EqualTo(0.0).Within(1e-3),
                        "a low-end endstop homes X to the axis minimum of M208 (RRF GCodes4.cpp AxisMinimum)");
        });
    }

    /// <summary>
    /// A homing macro that only runs G92 still homes the axis, because G92 marks every axis it sets
    /// as homed
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes::SetPositions for G92 (GCodes3.cpp) does
    /// axesHomed |= AxesAssumedHomed(axesIncluded) for the axes given, which for a Cartesian machine
    /// is all of them, so move.axes[1].homed is true and G28 Y succeeds without a homing move
    /// </remarks>
    [Test]
    public async Task G28UsingG92MacroMarksAxisHomed()
    {
        await using Bench bench = await StartBenchAsync(prepareSd: sd => sd.WriteSys("homey.g", "G92 Y0\n"));

        string reply = await bench.Host.ExecuteCodeAsync("G28 Y");
        Assert.That(reply.Trim(), Is.Empty, "G28 Y completed without error");
        bool homed = await bench.Host.ReadModelAsync(model => model.Move.Axes[1].Homed);
        double position = await bench.Host.ReadModelAsync(model => model.Move.Axes[1].MachinePosition!.Value);
        Assert.Multiple(() =>
        {
            Assert.That(homed, Is.True,
                        "G92 Y0 marks move.axes[1].homed (RRF GCodes3.cpp SetPositions, axesHomed |= AxesAssumedHomed)");
            Assert.That(position, Is.EqualTo(0.0).Within(1e-3),
                        "G92 Y0 sets move.axes[1].machinePosition to 0 (RRF GCodes3.cpp SetPositions)");
        });
    }
}
