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
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>
    /// Answer every ReadInputsRequest with one reading for the handle it asks about, as a board
    /// answers the read RepRapFirmware's <c>RemoteZProbe::GetCalibratedReading</c> sends a scanning probe
    /// </summary>
    internal static void AnswerReadInputs(ScriptedCanMaster canMaster, int reading)
        => canMaster.OnCanMessage((ushort)CanMessageType.ReadInputsRequest, (fake, header, payload) =>
        {
            CanMessageReadInputsRequest request = CanMessageSerializer.Deserialize<CanMessageReadInputsRequest>(payload);
            CanMessageReadInputsReplyV0 reply = default;
            reply.ResultCode = CodeResult.Ok;
            reply.NumReported = 1;
            reply.Results[0].Handle = request.Pattern;
            reply.Results[0].Reading = reading;
            byte[] whole = new byte[Marshal.SizeOf<CanMessageReadInputsReplyV0>()];
            CanMessageSerializer.Serialize(in reply, whole);
            fake.InjectCanResponse(header.TxToken, (ushort)CanMessageType.ReadInputsReplyV0, header.DstAddress,
                                   whole.AsSpan(0, (int)reply.GetActualDataLength()).ToArray());
        });

    /// <summary>
    /// Trigger a probe where a dive has travelled a given distance, and stop the dive there as the
    /// controller reports a stop it made itself
    /// </summary>
    /// <param name="bench">The bench the dive is running on</param>
    /// <param name="dive">The probing move</param>
    /// <param name="distance">How far along the dive the probe triggers, in the units of its <c>TotalDistance</c> (mm)</param>
    /// <param name="probeNumber">The probe whose input closes</param>
    /// <remarks>
    /// The stop carries the step clock time at which the dive's profile reaches the distance, so the
    /// engine puts the head exactly there whatever the scenario's own timing, and the stopped height
    /// is deterministic. The report waits for that time to pass, as a real trigger cannot be reported
    /// before it happens. The dive must start at rest and the distance must come before it decelerates,
    /// and the dive must have been scheduled after <see cref="WaitForClockSyncAsync"/>
    /// </remarks>
    internal static async Task TriggerProbeAtAsync(Bench bench, ScheduledMove dive, float distance, int probeNumber = 0)
    {
        await WaitForClockSyncAsync(bench);
        ScheduleMoveHeader header = dive.Header;
        Assert.That(distance, Is.InRange(0.0f, header.DecelStartDistance), "the probe triggers before the dive decelerates");
        double clocks = distance <= header.AccelDistance
            ? Math.Sqrt(2.0 * distance / header.Acceleration)
            : header.AccelClocks + ((distance - header.AccelDistance) / header.TopSpeed);
        uint whenTriggered = header.WhenToExecute + (uint)Math.Round(clocks);

        await bench.CanMaster.WaitUntilAsync(() => (int)(bench.CanMaster.Clock.MasterClock - whenTriggered) > 0, 20_000,
                                             $"the dive reaching {distance} mm");
        bench.CanMaster.InjectInputChange(Machines.DriverBoard, RemoteProbes.HandleFor(probeNumber), active: true);
        bench.CanMaster.InjectMotionStopped(whenTriggered, dive.MoveId,
                                            dive.Drivers.Select(driver => (driver.BoardAddress, driver.DriverNumber)).ToArray());
    }

    /// <summary>
    /// Wait until the engine's model of the controller's step clock is trusted. Until then a move's
    /// start time and a stop's trigger time are not in the same clock, and the engine places a stop
    /// where the motors are when the report arrives rather than at its timestamp
    /// </summary>
    internal static Task WaitForClockSyncAsync(Bench bench)
    {
        DuetControlServer.Link.Native.NativeLink link = bench.Host.Services.GetRequiredService<DuetControlServer.Link.Native.NativeLink>();
        return bench.CanMaster.WaitUntilAsync(() => link.GetClockStats().Synced != 0, 20_000, "the step clock model to synchronise");
    }
}
