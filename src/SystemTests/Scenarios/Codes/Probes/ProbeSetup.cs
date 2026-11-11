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

namespace SystemTests.Scenarios.Codes.Probes;

/// <summary>X, Y and Z with an endstop and a probe on board 1, and the moves that watch them</summary>
internal static class ProbeSetup
{
    /// <summary>A low-end switch endstop for X on board 1, as M574 configures one</summary>
    internal static readonly string XEndstopLine = $"M574 X1 S1 P\"{Expansion.GpioIn(Machines.DriverBoard, 0)}\"";

    /// <summary>An unfiltered digital Z probe on board 1, as M558 configures one</summary>
    internal static readonly string ProbeLine = $"M558 K0 P8 C\"{Expansion.GpioIn(Machines.DriverBoard, 1)}\"";

    /// <summary>Start a bench on X, Y and Z plus per-test configuration lines and SD files</summary>
    internal static Task<Bench> StartBenchAsync(string configExtra = "", Action<VirtualSd>? prepareSd = null)
        => Bench.With(Machines.Xyz, configExtra).WithSd(prepareSd).StartAsync();

    /// <summary>
    /// The scheduled moves that watch an input, in order: a homing or probing move carries the stop
    /// input per driver, so a nonzero handle is what tells them apart from travel moves
    /// </summary>
    internal static (uint MoveId, (byte Board, byte Driver)[] Drivers)[] ArmedMoves(ScriptedCanMaster canMaster)
        => canMaster.SbcPackets(SbcRequest.ScheduleMove)
                    .Select(p => p.DecodeScheduleMove())
                    .Where(m => m.Drivers.Any(d => d.StopOnHandle != 0))
                    .Select(m => (m.Header.MoveId,
                                  m.Drivers.Where(d => d.StopOnHandle != 0)
                                           .Select(d => (d.BoardAddress, d.DriverNumber))
                                           .ToArray()))
                    .ToArray();

    /// <summary>Wait for the (index + 1)th endstop- or probe-armed move to be scheduled</summary>
    internal static async Task<(uint MoveId, (byte Board, byte Driver)[] Drivers)> WaitForArmedMoveAsync(
        ScriptedCanMaster canMaster, int index, string what)
    {
        await canMaster.WaitUntilAsync(() => ArmedMoves(canMaster).Length > index, 20_000, what);
        return ArmedMoves(canMaster)[index];
    }

    /// <summary>Stop an armed move's drivers, as the controller reports a stop it performed</summary>
    internal static void StopMove(ScriptedCanMaster canMaster, (uint MoveId, (byte Board, byte Driver)[] Drivers) move)
        => canMaster.InjectMotionStopped(canMaster.Clock.MasterClock, move.MoveId, move.Drivers);

    /// <summary>One of a probe's axis offsets, or null if the probe has no offset for that axis</summary>
    internal static float? ProbeOffset(Probe probe, int axis)
        => axis < probe.Offsets.Count ? probe.Offsets[axis] : null;
}
