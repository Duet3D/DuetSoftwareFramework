using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Link;
using DuetControlServer.Motion;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// The Z probe and bed compensation M-codes, ported from RepRapFirmware's <c>GCodes::HandleMcode</c>
/// </summary>
/// <remarks>
/// <para>
/// A probe here is always a switch or an analog input on a CAN-connected expansion board, because
/// that is the only kind of hardware there is. RepRapFirmware supports probes on the main board too
/// and its <c>ZProbe</c> class hierarchy exists mostly to separate the two; what is left is
/// <c>RemoteZProbe</c>, which is an input monitor with a trigger height attached, so there is no
/// hierarchy here.
/// </para>
/// <para>
/// Everything a probe knows lives in <c>sensors.probes[]</c>, including the port, so a machine can be
/// rebuilt from the object model alone.
/// </para>
/// </remarks>
internal partial class MCodeHandler
{
    /// <summary>
    /// Most times M558 A may ask for a point to be probed, as in RepRapFirmware's <c>MaxTapsLimit</c>
    /// </summary>
    private const int MaxProbeTaps = 31;

    /// <summary>
    /// M558: configure a Z probe
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// P and C together create the probe; every other parameter configures the one that is already
    /// there. That split is RepRapFirmware's, and it is why naming a port without a type is an error
    /// rather than a change to the existing probe: the type decides what the board is asked to watch
    /// </remarks>
    private async ValueTask<Message> HandleProbeConfigAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.MinorNumber >= 0)
        {
            // M558.1 and M558.2 calibrate a scanning probe, which needs the probe to be read back
            // over CAN while it moves. Nothing here reads a probe yet
            return new Message(MessageType.Error, $"M558.{code.MinorNumber} is not supported");
        }

        int probeNumber = code.GetInt('K', 0);
        if (probeNumber is < 0 || probeNumber >= RemoteProbes.MaxProbes)
        {
            return new Message(MessageType.Error, $"Z probe number out of range (0..{RemoteProbes.MaxProbes - 1})");
        }

        bool seenType = code.TryGetInt('P', out int typeNumber);
        bool seenPort = code.TryGetString('C', out string? port);
        if (seenType && !Enum.IsDefined((ProbeType)typeNumber))
        {
            return new Message(MessageType.Error, $"Invalid Z probe type {typeNumber}");
        }

        ProbeType type = seenType ? (ProbeType)typeNumber : ProbeType.None;
        if (seenType && ProbeTypeRefusal(type) is string typeError)
        {
            return new Message(MessageType.Error, typeError);
        }

        if (seenPort && !seenType)
        {
            return new Message(MessageType.Error, "Missing Z probe type number");
        }

        // Checked before anything is written, so a port that cannot work leaves no half-configured
        // probe behind
        if (seenPort && !string.IsNullOrWhiteSpace(port))
        {
            if (!RemoteEndstops.TrySplitPort(port, "Z probe port", out _, out _, out string? portError))
            {
                return new Message(MessageType.Error, portError);
            }
        }

        if (seenType && type is not (ProbeType.None or ProbeType.ZMotorStall) && !seenPort)
        {
            // The board has to be told which pin to watch, and there is no default: a probe with no
            // port would look configured and never trigger
            bool hasPort;
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                Probe? existing = probeNumber < model.Sensors.Probes.Count ? model.Sensors.Probes[probeNumber] : null;
                hasPort = !string.IsNullOrWhiteSpace(existing?.Port);
            }
            if (!hasPort)
            {
                return new Message(MessageType.Error, "Missing Z probe pin name");
            }
        }

        if (!await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
        {
            throw new OperationCanceledException();
        }

        string? monitorPort = null;
        string? report = null;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Probe? probe = probeNumber < model.Sensors.Probes.Count ? model.Sensors.Probes[probeNumber] : null;
            if (probe is null && !seenType)
            {
                return new Message(MessageType.Error, $"Z probe {probeNumber} not found");
            }

            if (seenType)
            {
                probe = GetOrCreateProbe(probeNumber);
                probe.Type = type;
            }

            if (seenPort)
            {
                probe!.Port = port;
            }

            bool seen = seenType || seenPort;
            seen |= ApplyProbeParameters(code, probe!);

            if (seen)
            {
                // Only ask the board for a monitor once the object model agrees with what is being
                // asked for, so a rejected port leaves a probe that says what it is watching
                monitorPort = WatchableProbePort(probe!);
            }
            else
            {
                report = DescribeProbe(probeNumber, probe!);
            }
        }

        Message monitorReply = monitorPort is not null
            ? await CreateProbeMonitorAsync(probeNumber, monitorPort, cancellationToken)
            : new Message();
        if (monitorReply.Type == MessageType.Error)
        {
            return monitorReply;
        }

        // A board that took the port but had something to say about it is reported too, since M558
        // otherwise looks like it configured exactly what was asked for
        return new[] { monitorReply, new Message(MessageType.Success, report ?? string.Empty) }.ToMessage();
    }

    /// <summary>
    /// Why a probe type cannot be used, or null if it can
    /// </summary>
    /// <param name="type">The type</param>
    /// <returns>The reason</returns>
    /// <remarks>
    /// RepRapFirmware's <c>RemoteZProbe::Create</c> allows types 1, 8, 9 and 11 on an expansion board,
    /// because those are the ones an input monitor can express. The others need the probe to be read
    /// or driven by the board running the motion, which is not where any probe is here
    /// </remarks>
    private static string? ProbeTypeRefusal(ProbeType type) => type switch
    {
        ProbeType.None or ProbeType.ZMotorStall => null,
        ProbeType.Analog or ProbeType.UnfilteredDigital or ProbeType.BLTouch or ProbeType.ScanningAnalog => null,
        ProbeType.EndstopSwitch_Obsolete or ProbeType.E1Switch_Obsolete or ProbeType.ZSwitch_Obsolete
            => $"Z probe type {(int)type} is obsolete - use type {(int)ProbeType.UnfilteredDigital} instead",
        _ => $"Only Z probe types {(int)ProbeType.Analog}, {(int)ProbeType.UnfilteredDigital}, "
             + $"{(int)ProbeType.BLTouch} and {(int)ProbeType.ScanningAnalog} are supported on expansion boards"
    };

    /// <summary>
    /// Apply the M558 parameters that describe how a probe is used rather than what it is
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="probe">The probe</param>
    /// <returns>True if anything was set</returns>
    /// <remarks>The caller must hold the object model write lock</remarks>
    private static bool ApplyProbeParameters(Commands.Code code, Probe probe)
    {
        bool seen = false;

        if (code.TryGetFloatArray('H', out float[]? diveHeights) && diveHeights.Length > 0)
        {
            // One value sets both, so that M558 H5 raises the probe for every tap rather than only
            // the first, which is what it read as before multi-tapping had its own dive height
            probe.DiveHeights[0] = diveHeights[0];
            probe.DiveHeights[1] = diveHeights.Length > 1 ? diveHeights[1] : diveHeights[0];
            seen = true;
        }

        if (code.TryGetFloatArray('F', out float[]? speeds) && speeds.Length > 0)
        {
            // Given in mm/min like every other feed rate, held in mm/s like every other speed here
            while (probe.Speeds.Count < 2)
            {
                probe.Speeds.Add(0.0f);
            }
            probe.Speeds[0] = speeds[0] / SecondsPerMinute;
            probe.Speeds[1] = (speeds.Length > 1 ? speeds[1] : speeds[0]) / SecondsPerMinute;
            if (speeds.Length > 2)
            {
                // The third speed is how fast a scanning probe travels while mapping, which only a
                // scanning probe has, so the slot only exists once one is asked for
                while (probe.Speeds.Count < 3)
                {
                    probe.Speeds.Add(0.0f);
                }
                probe.Speeds[2] = speeds[2] / SecondsPerMinute;
            }
            seen = true;
        }

        if (code.TryGetFloat('T', out float travelSpeed))
        {
            probe.TravelSpeed = travelSpeed;
            seen = true;
        }

        if (code.TryGetInt('B', out int disableHeaters))
        {
            probe.DisablesHeaters = disableHeaters == 1;
            seen = true;
        }

        if (code.TryGetFloat('R', out float recoveryTime))
        {
            probe.RecoveryTime = recoveryTime;
            seen = true;
        }

        if (code.TryGetFloat('S', out float tolerance))
        {
            probe.Tolerance = tolerance;
            seen = true;
        }

        if (code.TryGetInt('A', out int maxTaps))
        {
            probe.MaxProbeCount = Math.Clamp(maxTaps, 1, MaxProbeTaps);
            seen = true;
        }

        return seen;
    }

    /// <summary>
    /// The port an expansion board should be asked to watch for a probe, or null if there is none
    /// </summary>
    /// <param name="probe">The probe</param>
    /// <returns>The port</returns>
    /// <remarks>
    /// A motor stall probe is detected by the driver rather than by an input, and a probe of type
    /// none is a placeholder that G30 refuses, so neither has anything to watch
    /// </remarks>
    private static string? WatchableProbePort(Probe probe)
        => probe.Type is ProbeType.None or ProbeType.ZMotorStall || string.IsNullOrWhiteSpace(probe.Port)
           ? null
           : probe.Port;

    /// <summary>
    /// Ask the board carrying a probe's port to watch it and report changes
    /// </summary>
    /// <param name="probeNumber">Probe number</param>
    /// <param name="port">Port as given to M558</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What the board said about it, empty if it accepted without comment</returns>
    private async ValueTask<Message> CreateProbeMonitorAsync(int probeNumber, string port, CancellationToken cancellationToken)
    {
        if (!RemoteEndstops.TrySplitPort(port, "Z probe port", out byte board, out string localPort,
                                         out string? error))
        {
            return new Message(MessageType.Error, error);
        }

        int threshold;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            Probe probe = model.Sensors.Probes[probeNumber]!;

            // A nonzero threshold is what tells the board to treat the handle as analog, which is
            // why a digital probe sends zero rather than its own threshold: it reports a level, and
            // asking it to compare that level against anything would stop it reporting at all. The
            // threshold itself is G31 P, defaulting to RepRapFirmware's DefaultZProbeADValue
            threshold = probe.Type is ProbeType.Analog or ProbeType.ScanningAnalog ? probe.Threshold : 0;
        }

        CanMessageCreateInputMonitorV1 message = new()
        {
            Handle = RemoteProbes.HandleFor(probeNumber),
            Threshold = threshold,

            // Created idle, and raised to the probing rate by ProbeArming for the duration of a tap.
            // RepRapFirmware creates at the probing rate and only slows the probe down once it has
            // finished probing for the first time, which leaves a configured but unused probe
            // reporting every change it sees
            MinInterval = (ushort)Motion.ProbeArming.InactiveReportInterval
        };
        CanText.SetString(message.PinName, localPort);

        CanResponse response = await linkInterface.SendCanMessageAsync(board, in message, CanMessageType.StandardReply,
                                                                      cancellationToken: cancellationToken);
        Message reply = response.ToMessage();
        if (reply.Type != MessageType.Error)
        {
            // As for an endstop: the board reports changes from here on, so a probe already reading
            // above its threshold when it was configured would read as clear until it moved. A
            // probing move checks the probe before it starts, and would not have noticed
            await expansionBoardManager.NoteMonitorCreatedAsync(message.Handle, response.Extra != 0,
                                                                cancellationToken);
        }
        return reply;
    }

    /// <summary>
    /// The probe at the given index, adding it if this is the first time it is configured
    /// </summary>
    /// <param name="probeNumber">Probe number</param>
    /// <returns>The probe</returns>
    /// <remarks>The caller must hold the object model write lock</remarks>
    private Probe GetOrCreateProbe(int probeNumber)
    {
        while (model.Sensors.Probes.Count <= probeNumber)
        {
            model.Sensors.Probes.Add(null);
        }
        return model.Sensors.Probes[probeNumber] ??= new Probe();
    }

    /// <summary>
    /// Describe a probe the way M558 with no parameters does
    /// </summary>
    /// <param name="probeNumber">Probe number</param>
    /// <param name="probe">The probe</param>
    /// <returns>The description</returns>
    private static string DescribeProbe(int probeNumber, Probe probe)
    {
        StringBuilder builder = new();
        builder.Append(CultureInfo.InvariantCulture, $"Z Probe {probeNumber}: type {(int)probe.Type}");
        if (!string.IsNullOrWhiteSpace(probe.Port))
        {
            builder.Append(CultureInfo.InvariantCulture, $", input pin {probe.Port}");
        }
        builder.Append(CultureInfo.InvariantCulture,
                       $", dive heights {probe.DiveHeights[0]:F1},{probe.DiveHeights[1]:F1}mm");
        builder.Append(CultureInfo.InvariantCulture,
                       $", probe speeds {probe.Speeds[0] * SecondsPerMinute:F0},{probe.Speeds[1] * SecondsPerMinute:F0}");
        if (probe.Speeds.Count > 2)
        {
            builder.Append(CultureInfo.InvariantCulture, $",{probe.Speeds[2] * SecondsPerMinute:F0}");
        }
        builder.Append(CultureInfo.InvariantCulture, $"mm/min, travel speed {probe.TravelSpeed:F0}mm/min");
        builder.Append(CultureInfo.InvariantCulture, $", recovery time {probe.RecoveryTime:F2} sec");
        builder.Append(probe.DisablesHeaters ? ", heaters suspended" : ", heaters normal");
        builder.Append(CultureInfo.InvariantCulture, $", max taps {probe.MaxProbeCount}, max diff {probe.Tolerance:F2}");
        return builder.ToString();
    }

    /// <summary>
    /// M401: deploy a Z probe
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private ValueTask<Message> HandleDeployProbeAsync(Commands.Code code, CancellationToken cancellationToken)
        => MoveProbeAsync(code, DeployProbeMacro, deploying: true, cancellationToken);

    /// <summary>
    /// M402: retract a Z probe
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private ValueTask<Message> HandleRetractProbeAsync(Commands.Code code, CancellationToken cancellationToken)
        => MoveProbeAsync(code, RetractProbeMacro, deploying: false, cancellationToken);

    /// <summary>Macro that lowers a Z probe into place</summary>
    private const string DeployProbeMacro = "deployprobe";

    /// <summary>Macro that lifts a Z probe out of the way</summary>
    private const string RetractProbeMacro = "retractprobe";

    /// <summary>
    /// Deploy or retract a Z probe by running the macro that moves it
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="macro">Base name of the macro</param>
    /// <param name="deploying">Whether the probe ends up deployed</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// <para>
    /// Deploying is what the probe does mechanically; <c>deployedByUser</c> is why. A probe deployed
    /// by M401 stays deployed until M402 asks for it back, which is what stops G30 from retracting a
    /// probe the user put down on purpose. That flag is cleared before the macro runs so the macro
    /// runs whatever the probe's current state says, exactly as RepRapFirmware does.
    /// </para>
    /// <para>
    /// The numbered macro is tried first so a machine with two probes can move them separately.
    /// RepRapFirmware passes the probe number to the unnumbered macro in a <c>K</c> variable;
    /// meta G-code variables are not ported yet, so the unnumbered macro runs without it
    /// </para>
    /// </remarks>
    private async ValueTask<Message> MoveProbeAsync(Commands.Code code, string macro, bool deploying,
                                                     CancellationToken cancellationToken)
    {
        int probeNumber = code.GetInt('P', 0);
        if (probeNumber < 0 || probeNumber >= RemoteProbes.MaxProbes)
        {
            return new Message(MessageType.Error, $"Z probe number out of range (0..{RemoteProbes.MaxProbes - 1})");
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Probe? probe = probeNumber < model.Sensors.Probes.Count ? model.Sensors.Probes[probeNumber] : null;
            if (probe is null || probe.Type == ProbeType.None)
            {
                return new Message();           // nothing to move, which RepRapFirmware also passes over quietly
            }
            probe.DeployedByUser = false;
        }

        if (!await macroRunner.TryRunAsync(code.Channel, $"{macro}{probeNumber}.g", code, cancellationToken: cancellationToken))
        {
            await macroRunner.TryRunAsync(code.Channel, $"{macro}.g", code, cancellationToken: cancellationToken);
        }

        if (deploying)
        {
            using (await model.AccessReadWriteAsync(cancellationToken))
            {
                model.Sensors.Probes[probeNumber]!.DeployedByUser = true;
            }
        }
        return new Message();
    }

    /// <summary>
    /// M851: set or report the Z probe offset, for Marlin compatibility
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// Marlin's Z offset is the negative of RepRapFirmware's trigger height, so this is G31 Z with
    /// the sign flipped and always for probe 0
    /// </remarks>
    private async ValueTask<Message> HandleProbeOffsetAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Probe? probe = model.Sensors.Probes.Count > 0 ? model.Sensors.Probes[0] : null;
            if (probe is null)
            {
                return new Message(MessageType.Error, "Z probe 0 not found");
            }

            if (!code.TryGetFloat('Z', out float offset))
            {
                return new Message(MessageType.Success, $"Z probe offset is {-probe.TriggerHeight:F2}mm");
            }

            probe.TriggerHeight = -offset;
            for (int axis = 0; axis < model.Move.Axes.Count && axis < probe.Offsets.Count; axis++)
            {
                if (model.Move.Axes[axis].Letter == 'Z')
                {
                    probe.Offsets[axis] = offset;
                }
            }
        }
        return new Message();
    }

    /// <summary>
    /// How long to wait between checks while M577 waits for an input
    /// </summary>
    /// <remarks>
    /// RepRapFirmware re-runs the code every time round its main loop. Here the check is a poll on
    /// the object model, which the expansion boards update as their inputs change, so the interval
    /// only decides how promptly the wait ends
    /// </remarks>
    private static readonly TimeSpan WaitForInputInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// M577: wait for an endstop or general-purpose input to reach a state
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// Every named input has to be in the wanted state at the same time, not one after another: the
    /// code is used to wait for a door to be shut and a guard to be in place together
    /// </remarks>
    private async ValueTask<Message> HandleWaitForInputAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        // S defaults to 1, so M577 with no S waits for the inputs to become active
        bool activeHigh = code.GetInt('S', 1) >= 1;
        bool seenPorts = code.TryGetIntArray('P', out int[]? ports);

        List<int> axes = [];
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            for (int axis = 0; axis < model.Move.Axes.Count; axis++)
            {
                if (code.HasParameter(model.Move.Axes[axis].Letter))
                {
                    axes.Add(axis);
                }
            }
        }

        if (axes.Count == 0 && !seenPorts)
        {
            return new Message();               // nothing named, so there is nothing to wait for
        }

        while (true)
        {
            bool satisfied = true;
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                foreach (int axis in axes)
                {
                    Endstop? endstop = axis < model.Sensors.Endstops.Count ? model.Sensors.Endstops[axis] : null;

                    // An axis with no endstop can never reach the wanted state, so waiting for it
                    // would hang. RepRapFirmware treats a missing endstop as not triggered
                    satisfied &= (endstop?.Triggered ?? false) == activeHigh;
                }

                if (seenPorts)
                {
                    foreach (int port in ports!)
                    {
                        // A port that does not exist is passed over rather than waited on, as in
                        // RepRapFirmware: it cannot report a state either way
                        if (port >= 0 && port < model.Sensors.GpIn.Count && model.Sensors.GpIn[port] is GpInputPort input)
                        {
                            satisfied &= (input.Value > 0.0f) == activeHigh;
                        }
                    }
                }
            }

            if (satisfied)
            {
                return new Message();
            }
            await Task.Delay(WaitForInputInterval, cancellationToken);
        }
    }
}
