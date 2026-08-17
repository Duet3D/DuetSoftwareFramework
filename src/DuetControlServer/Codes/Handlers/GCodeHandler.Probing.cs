using DuetAPI.ObjectModel;
using DuetControlServer.Motion;
using DuetControlServer.Motion.Native;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using static DuetControlServer.Motion.AxisIndices;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// Probing: driving the nozzle at the bed until the probe triggers, and what is done with the result
/// </summary>
/// <remarks>
/// <para>
/// A probing move is a homing move that stops on a probe instead of an endstop, so the mechanism is
/// the one phase 5 already built: the controller watches the input and cuts the move short, and the
/// engine corrects the position for the latency of the report.
/// </para>
/// <para>
/// What is around it is the tapping loop. A probe does not give the same answer twice, so RepRapFirmware
/// probes repeatedly until two consecutive readings agree within the configured tolerance, and averages
/// what it accepted. This does the same, which is why <c>M558 A</c> and <c>M558 S</c> matter
/// </para>
/// </remarks>
internal sealed partial class GCodeHandler
{
    /// <summary>
    /// How often to check whether the probe's recovery time has elapsed
    /// </summary>
    private const float MinuteInSeconds = 60.0f;

    /// <summary>
    /// S values that mean something other than "set the Z origin"
    /// </summary>
    private const int ReportHeight = -1, SetToolOffset = -2, SetTriggerHeight = -3;

    /// <summary>
    /// G30: probe the bed
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// Without S the Z origin is set so that the probe's trigger height is where the nozzle now is,
    /// which is what makes G30 the way a machine is levelled. S-1 only reports, S-3 calibrates the
    /// probe against a Z axis that is already trusted, and S-2 needs tools, which are not ported
    /// </remarks>
    private async ValueTask<Message> HandleProbeAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.HasParameter('P'))
        {
            // A probe point index feeds the bed levelling and mesh tables, which are what G29 and
            // M671 build; neither exists yet, so accepting the point would silently discard it
            return new Message(MessageType.Error, "G30 P is not supported yet - use G30 without P");
        }

        int probeNumber = code.GetInt('K', 0);
        if (probeNumber < 0 || probeNumber >= RemoteProbes.MaxProbes)
        {
            return new Message(MessageType.Error, $"Z probe number out of range (0..{RemoteProbes.MaxProbes - 1})");
        }

        // S-4 or lower means the same as no S at all, as in RepRapFirmware
        int sValue = code.GetInt('S', -4);
        if (sValue >= 0)
        {
            sValue = -4;
        }
        if (sValue == SetToolOffset)
        {
            return new Message(MessageType.Error, "G30 S-2 needs a tool, and tools are not supported yet");
        }

        float heightOffset = code.GetFloat('H', 0.0f);

        if (!await planner.WaitForStandstillAsync(cancellationToken))
        {
            throw new OperationCanceledException();
        }

        ProbeSettings settings = await ReadProbeSettingsAsync(probeNumber, cancellationToken);
        if (settings.Refusal is not null)
        {
            return new Message(MessageType.Error, settings.Refusal);
        }

        await macroRunner.TryRunAsync(code.Channel, $"deployprobe{probeNumber}.g", code, cancellationToken: cancellationToken);

        try
        {
            (float? stoppedHeight, Message? error) = await TapAsync(code, probeNumber, settings, heightOffset, cancellationToken);
            if (error is not null)
            {
                return error;
            }
            return await ApplyProbeResultAsync(probeNumber, sValue, stoppedHeight!.Value, settings, cancellationToken);
        }
        finally
        {
            // However the probe ended, it has to come back up: leaving it deployed would have the
            // next travel move drag it across the bed
            await macroRunner.TryRunAsync(code.Channel, $"retractprobe{probeNumber}.g", code, cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// What a probing move needs to know about the probe, read once so the loop does not keep taking
    /// the object model lock
    /// </summary>
    /// <param name="Refusal">Why the probe cannot be used, or null if it can</param>
    /// <param name="ZAxis">Index of the Z axis</param>
    /// <param name="DiveHeights">How far above the trigger point each tap starts</param>
    /// <param name="Speeds">Probing speed of each tap, mm/s</param>
    /// <param name="TravelSpeed">Speed of the moves between taps, mm/s</param>
    /// <param name="MaxTaps">Most taps to make before giving up on agreement</param>
    /// <param name="Tolerance">How closely two taps have to agree, mm</param>
    /// <param name="TriggerHeight">Where the probe triggers relative to the nozzle, mm</param>
    /// <param name="RecoveryTime">How long to wait before each tap, seconds</param>
    /// <param name="ZMin">Lowest the Z axis may be driven to</param>
    /// <param name="IsStallProbe">
    /// Whether the probe is the drivers of Z stalling rather than an input on a pin, which is what
    /// decides whether the drivers have to be armed before each tap
    /// </param>
    private readonly record struct ProbeSettings(
        string? Refusal, int ZAxis, float[] DiveHeights, float[] Speeds, float TravelSpeed,
        int MaxTaps, float Tolerance, float TriggerHeight, float RecoveryTime, float ZMin,
        bool IsStallProbe = false);

    /// <summary>
    /// Read what a probing move needs to know
    /// </summary>
    /// <param name="probeNumber">Probe number</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The settings, or a refusal</returns>
    private async ValueTask<ProbeSettings> ReadProbeSettingsAsync(int probeNumber, CancellationToken cancellationToken)
    {
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            Probe? probe = probeNumber < model.Sensors.Probes.Count ? model.Sensors.Probes[probeNumber] : null;
            if (probe is null)
            {
                return new ProbeSettings($"Z probe {probeNumber} not found", 0, [], [], 0, 0, 0, 0, 0, 0);
            }
            if (probe.Type == ProbeType.None)
            {
                return new ProbeSettings($"Z probe {probeNumber} cannot stop a move", 0, [], [], 0, 0, 0, 0, 0, 0);
            }

            int zAxis = ZAxisIndex(model.Move);
            if (zAxis < 0)
            {
                return new ProbeSettings("The machine has no Z axis", 0, [], [], 0, 0, 0, 0, 0, 0);
            }

            return new ProbeSettings(
                null, zAxis,
                [probe.DiveHeights[0], probe.DiveHeights.Count > 1 ? probe.DiveHeights[1] : probe.DiveHeights[0]],
                [probe.Speeds[0], probe.Speeds.Count > 1 ? probe.Speeds[1] : probe.Speeds[0]],
                probe.TravelSpeed / MinuteInSeconds,
                Math.Max(1, probe.MaxProbeCount),
                probe.Tolerance,
                probe.TriggerHeight,
                probe.RecoveryTime,
                model.Move.Axes[zAxis].Min,
                probe.Type == ProbeType.ZMotorStall);
        }
    }

    /// <summary>
    /// Probe until two consecutive taps agree, and return the height the probe stopped at
    /// </summary>
    /// <param name="code">The code that asked for it, for the macros</param>
    /// <param name="probeNumber">Probe number</param>
    /// <param name="settings">What the probe is configured to do</param>
    /// <param name="heightOffset">The H parameter, subtracted from every reading</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The averaged stopped height, or the reason there is none</returns>
    /// <remarks>
    /// Two consecutive taps within the tolerance are accepted and averaged. Running out of taps
    /// without agreement is not an error - RepRapFirmware warns and uses the mean of what it has,
    /// because a bed that reads within a few microns of itself is usually good enough to print on
    /// </remarks>
    private async ValueTask<(float? StoppedHeight, Message? Error)> TapAsync(
        Commands.Code code, int probeNumber, ProbeSettings settings, float heightOffset,
        CancellationToken cancellationToken)
    {
        float sum = 0.0f, previous = 0.0f;
        int taps = 0;

        while (taps < settings.MaxTaps)
        {
            // A BLTouch has to be redeployed before each tap, because triggering is what retracts it
            if (taps > 0)
            {
                await macroRunner.TryRunAsync(code.Channel, $"deployprobe{probeNumber}.g", code,
                                              cancellationToken: cancellationToken);
            }

            if (settings.RecoveryTime > 0.0f)
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.RecoveryTime), cancellationToken);
            }

            // Only an input can be closed before the move starts. A stall is a driver's judgement
            // about a move that is running, so there is nothing for it to be already triggered by -
            // and the latch that answers for it describes the previous tap until this one clears it
            if (!settings.IsStallProbe &&
                await IsProbeTriggeredAsync(probeNumber, settings.ZAxis, cancellationToken))
            {
                return (null, new Message(MessageType.Error, "Probe already triggered before probing move started"));
            }

            // The first tap starts from the first dive height; the rest start from the second, which
            // is what lets a machine take one long approach and then several short ones
            int tapIndex = Math.Min(taps, 1);
            await MoveToZAsync(settings.ZAxis, ProbeStartHeight(settings, tapIndex), settings.TravelSpeed,
                               checkProbe: false, probeNumber, [], cancellationToken);

            // A stall probe has to have the drivers that move Z told what speed to expect, which is a
            // CAN round trip and so cannot happen while the move is being built. The speed is this
            // tap's, because that is what the driver compares against
            IReadOnlyList<WatchedDriver> stallDrivers = settings.IsStallProbe
                ? await StallProbeDriversAsync(settings.ZAxis, settings.Speeds[tapIndex], cancellationToken)
                : [];
            EndstopArmingState arming = new();

            // The probe on a board is told the threshold to compare against and asked to report more
            // often, for the duration of the tap. A stall probe has no input, so this finds nothing
            // to tell
            ProbeArming.ProbeMonitor probeMonitor = default;
            bool armProbe;
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                Probe? probe = probeNumber < model.Sensors.Probes.Count ? model.Sensors.Probes[probeNumber] : null;
                armProbe = probe is not null && ProbeArming.TryCapture(probe, probeNumber, out probeMonitor);
            }

            float target = settings.ZMin - settings.DiveHeights[0] + settings.TriggerHeight;
            try
            {
                if (stallDrivers.Count > 0)
                {
                    await StallArming.ArmAsync(stallDrivers, arming, linkInterface, cancellationToken);
                }

                if (armProbe)
                {
                    await ProbeArming.StartAsync(probeMonitor, linkInterface, cancellationToken);
                }

                if (!await MoveToZAsync(settings.ZAxis, target, settings.Speeds[tapIndex],
                                        checkProbe: true, probeNumber, stallDrivers, cancellationToken))
                {
                    return (null, new Message(MessageType.Error, "Failed to arm the Z probe"));
                }
            }
            finally
            {
                // However the tap ended. A driver left armed reports a stall during an ordinary move,
                // and a probe left at the probing rate reports every change it sees for the rest of
                // the job
                await StallArming.ReleaseAsync(arming, linkInterface, logger, CancellationToken.None);
                if (armProbe)
                {
                    await ProbeArming.StopAsync(probeMonitor, linkInterface, logger, CancellationToken.None);
                }
            }

            if (!await IsProbeTriggeredAsync(probeNumber, settings.ZAxis, cancellationToken))
            {
                return (null, new Message(MessageType.Error, "Probe was not triggered during probing move"));
            }

            float stopped = await GetMachineZAsync(settings.ZAxis, cancellationToken) - heightOffset;
            await RecordStoppedHeightAsync(probeNumber, stopped, cancellationToken);

            taps++;
            sum += stopped;

            if (taps > 1 && MathF.Abs(stopped - previous) <= settings.Tolerance)
            {
                // Two readings that agree, so the last two are what the bed is; earlier taps were the
                // probe settling and would drag the average towards them
                return ((stopped + previous) / 2.0f, null);
            }
            previous = stopped;

            // Back up to the dive height before tapping again, or before whatever comes next
            await MoveToZAsync(settings.ZAxis, stopped + settings.DiveHeights[1], settings.TravelSpeed,
                               checkProbe: false, probeNumber, [], cancellationToken);
        }

        return (sum / taps, null);
    }

    /// <summary>
    /// Where a tap starts from
    /// </summary>
    /// <param name="settings">What the probe is configured to do</param>
    /// <param name="tapIndex">Which tap, capped at one</param>
    /// <returns>The Z height</returns>
    private static float ProbeStartHeight(ProbeSettings settings, int tapIndex)
        => settings.ZMin + settings.DiveHeights[tapIndex] + settings.TriggerHeight;

    /// <summary>
    /// Move the Z axis, optionally stopping on the probe
    /// </summary>
    /// <param name="zAxis">Index of the Z axis</param>
    /// <param name="target">Where to move to</param>
    /// <param name="speed">Speed in mm/s</param>
    /// <param name="checkProbe">Whether the move stops when the probe triggers</param>
    /// <param name="probeNumber">Probe number</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>False if the move could not be armed on the probe</returns>
    /// <summary>
    /// The drivers a motor stall probe watches, and how fast each will turn
    /// </summary>
    /// <param name="zAxis">Index of the Z axis</param>
    /// <param name="speed">Probing speed of this tap, mm/s</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The drivers</returns>
    /// <remarks>
    /// The same list a stall-homed Z would watch, from the same place, because it is the same
    /// question: which drivers have to turn for Z to move. On a delta that is all three towers
    /// </remarks>
    private async ValueTask<IReadOnlyList<WatchedDriver>> StallProbeDriversAsync(
        int zAxis, float speed, CancellationToken cancellationToken)
    {
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            return EndstopPlanner.DriversMoving(model.Move, planner.Parameters.Geometry,
                                                planner.Parameters.SharedAxisCount(model.Move), zAxis,
                                                planner.Parameters.StepsPerMm, speed);
        }
    }

    private async ValueTask<bool> MoveToZAsync(int zAxis, float target, float speed, bool checkProbe,
                                               int probeNumber, IReadOnlyList<WatchedDriver> stallDrivers,
                                               CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            MoveSubmitResult result;

            using (await model.AccessReadWriteAsync(cancellationToken))
            {
                RawMove move = new()
                {
                    FeedRateMmPerSec = speed,
                    LinearAxesMentioned = true,
                    CheckEndstops = checkProbe,
                    ReduceAcceleration = checkProbe
                };

                // Everything else stays where it is, so the move is built from the current position
                for (int axis = 0; axis < model.Move.Axes.Count; axis++)
                {
                    move.Coords[axis] = model.Move.Axes[axis].MachinePosition ?? 0.0f;
                }
                move.Coords[zAxis] = target;

                if (checkProbe)
                {
                    Probe probe = model.Sensors.Probes[probeNumber]!;

                    // Every driver of the move goes, which is what RepRapFirmware's ZProbe answers
                    // whatever the geometry: on a delta the effector only comes down because all
                    // three towers do, so stopping one would tip it.
                    //
                    // A motor stall probe has no input to watch, so what stops the move is the stall
                    // report from the boards carrying the drivers that move Z - the same handle a
                    // stall-homed axis stops on, armed by the caller before the move
                    bool armed = probe.Type == ProbeType.ZMotorStall
                        ? RemoteEndstops.TryGetStallStopInput(stallDrivers, move.StopOnInput[0])
                        : RemoteProbes.TryGetStopInput(probe, probeNumber, StopAction.All, move.StopOnInput[0]);
                    if (!armed)
                    {
                        return false;
                    }
                    move.StopOnInput[0].Action = StopAction.All;

                    // What stopped the last probing move says nothing about this one. A switch probe
                    // is read from its own input so it does not need this; a stall probe has no
                    // input, and the latch is the only record that the move was stopped at all
                    using (planner.Lock())
                    {
                        planner.State.ArmEndstops();
                    }
                    for (int drive = 1; drive < move.StopOnInput.Length; drive++)
                    {
                        move.StopOnInput[drive].CopyFrom(move.StopOnInput[0]);
                    }

                    // What stopped the last move that watched something says nothing about this one
                    moveInterpreter.ArmCorrection(move);
                }

                result = planner.QueueMove(move);
            }

            switch (result)
            {
                case MoveSubmitResult.Queued:
                case MoveSubmitResult.NoMovement:
                    // A probing move is an endstop move armed on a probe handle, so it is waited for
                    // the same way: standstill, and then the wind-back the boards are doing on their
                    // own. RepRapFirmware uses the same WaitForEndstopOrProbingMoveToFinish for both
                    await WaitForSpecialMoveToFinishAsync(checkProbe, cancellationToken);
                    if (checkProbe)
                    {
                        // The move stopped short, so this side's idea of where Z is comes from the
                        // engine rather than from what was commanded
                        using (await model.AccessReadWriteAsync(cancellationToken))
                        {
                            using (planner.Lock())
                            {
                                // Under the planner lock, which is what a stop report takes before it
                                // records anything: what arrives after this belongs to a move whose
                                // height has already been read off, and applying it would move Z
                                // under the tap that has just been measured
                                endstopCorrection.ConcludeMove();

                                planner.ResyncFromEngine();
                                RedefineMachinePosition(zAxis, planner.Builder.StartCoordinates[zAxis]);
                            }
                        }
                    }
                    return true;

                case MoveSubmitResult.Rejected:
                    return false;

                default:
                    await Task.Delay(RingFullRetryDelay, cancellationToken);
                    break;
            }
        }
        return false;
    }

    /// <summary>Whether a probe is currently reading above its threshold</summary>
    /// <param name="probeNumber">Probe number</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if it is triggered</returns>
    private async ValueTask<bool> IsProbeTriggeredAsync(int probeNumber, int zAxis,
                                                        CancellationToken cancellationToken)
    {
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            Probe probe = model.Sensors.Probes[probeNumber]!;
            if (probe.Type != ProbeType.ZMotorStall)
            {
                return probe.Value.Count > 0 && probe.Value[0] >= probe.Threshold;
            }

            // A stall has no reading to compare: nothing writes sensors.probes[].value for it,
            // because a driver reporting a stall is not an input on a pin. What says it triggered is
            // that the move was stopped, which is what the latch records - and it is cleared before
            // each tap, so it can only mean this one.
            //
            // RepRapFirmware answers the same question from the live stalled-driver bitmap of its
            // local drivers. There are none here, which is why its own motor stall probe cannot work
            // on this architecture at all
            using (planner.Lock())
            {
                return zAxis >= 0 && zAxis < MotionLimits.MaxAxes &&
                       (planner.State.EndstopsTriggered & (1u << zAxis)) != 0;
            }
        }
    }

    /// <summary>Where the Z axis is now, in machine coordinates</summary>
    /// <param name="zAxis">Index of the Z axis</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The position</returns>
    private async ValueTask<float> GetMachineZAsync(int zAxis, CancellationToken cancellationToken)
    {
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            // An axis with no position at all cannot have been probed, but the caller only reaches
            // here after a probing move, which sets one
            return model.Move.Axes[zAxis].MachinePosition ?? 0.0f;
        }
    }

    /// <summary>Publish where the probe last stopped, which is what a client shows after G30 S-1</summary>
    /// <param name="probeNumber">Probe number</param>
    /// <param name="height">The height</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An awaitable task</returns>
    private async ValueTask RecordStoppedHeightAsync(int probeNumber, float height, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            model.Sensors.Probes[probeNumber]!.LastStopHeight = height;
        }
    }

    /// <summary>
    /// Do whatever the S parameter asked for with the height the probe stopped at
    /// </summary>
    /// <param name="probeNumber">Probe number</param>
    /// <param name="sValue">The S parameter</param>
    /// <param name="stoppedHeight">Where the probe stopped, in machine coordinates</param>
    /// <param name="settings">What the probe is configured to do</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> ApplyProbeResultAsync(int probeNumber, int sValue, float stoppedHeight,
                                                            ProbeSettings settings, CancellationToken cancellationToken)
    {
        if (sValue == ReportHeight)
        {
            return new Message(MessageType.Success,
                string.Create(CultureInfo.InvariantCulture, $"Stopped at height {stoppedHeight:F3} mm"));
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Probe probe = model.Sensors.Probes[probeNumber]!;
            if (sValue == SetTriggerHeight)
            {
                // The Z axis is trusted and the probe is not, so the probe takes the height the axis
                // says it stopped at. This is how a probe is calibrated against a homed machine
                probe.TriggerHeight = stoppedHeight;
                int zAxis = settings.ZAxis;
                if (zAxis < probe.Offsets.Count)
                {
                    probe.Offsets[zAxis] = -stoppedHeight;
                }
                return new Message(MessageType.Success,
                    string.Create(CultureInfo.InvariantCulture, $"Z probe trigger height set to {stoppedHeight:F3} mm"));
            }

            // The probe is trusted and the Z axis is not, so the axis is redefined: the nozzle is at
            // the trigger height now, whatever the axis thought before. That is what levels a machine
            Axis zAxisConfig = model.Move.Axes[settings.ZAxis];
            float correction = stoppedHeight - probe.TriggerHeight;

            using (planner.Lock())
            {
                // Taken from the planner rather than the object model: the object model's machine
                // position is live and this has to be measured from where the last move left Z
                float position = planner.Builder.StartCoordinates[settings.ZAxis] - correction;
                RedefineMachinePosition(settings.ZAxis, position);

                // Z has just been redefined, so "flat" has moved and the map has to be told where to.
                // Otherwise the height map immediately corrects the machine at the very point the
                // probe was used to zero it - it would fight the operation that set its own datum
                ZeroTheHeightMapHere(probe, settings);
            }
            zAxisConfig.Homed = true;
        }
        return new Message();
    }

    /// <summary>
    /// Normalise the height map to read zero at the point the probe just measured
    /// </summary>
    /// <param name="probe">The probe that was used</param>
    /// <param name="settings">What the probe is configured to do</param>
    /// <remarks>
    /// RepRapFirmware's <c>Move::SetZeroHeightError</c> call after a plain G30. The coordinates it
    /// wants are the probe's rather than the nozzle's, so the probe's offsets are added first and the
    /// axis skew applied, exactly as RepRapFirmware does before looking the point up. The caller must
    /// hold the object model write lock and the planner lock
    /// </remarks>
    private void ZeroTheHeightMapHere(Probe probe, ProbeSettings settings)
    {
        if (!bedCompensation.IsActive)
        {
            return;
        }

        int numAxes = planner.Parameters.SharedAxisCount(model.Move);
        float[] probePosition = new float[MotionLimits.MaxAxes];
        planner.Builder.StartCoordinates[..numAxes].CopyTo(probePosition);

        for (int axis = 0; axis < numAxes && axis < probe.Offsets.Count; axis++)
        {
            if (axis != settings.ZAxis)
            {
                probePosition[axis] += probe.Offsets[axis];
            }
        }
        AxisSkew.Apply(toolManager.Current, model.Move, probePosition, numAxes);

        (float axis0, float axis1) = bedCompensation.GridCoordinates(probePosition, numAxes);
        bedCompensation.SetZeroHeightError(axis0, axis1);
    }

    /// <summary>
    /// G29: probe the grid and build a height map
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// S selects what to do: S0 probes, S1 loads a map, S2 forgets one and S3 saves one. Without S,
    /// RepRapFirmware runs <c>mesh.g</c> if the machine has one, so that a bed that needs preparing
    /// before it is probed can say so; only if there is no such file does it probe directly
    /// </remarks>
    private async ValueTask<Message> HandleProbeGridAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!code.TryGetInt('S', out int sValue))
        {
            if (await macroRunner.TryRunAsync(code.Channel, MeshMacro, code, cancellationToken: cancellationToken))
            {
                return new Message();
            }
            sValue = 0;
        }

        switch (sValue)
        {
            case 0:
                return await ProbeGridAsync(code, cancellationToken);

            case 1:
            case 2:
            case 3:
                // The height map codes do exactly these three things, and doing them twice would be
                // two implementations to keep in step
                return new Message(MessageType.Error, sValue switch
                {
                    1 => "Use M375 to load a height map",
                    2 => "Use M561 to disable bed compensation",
                    _ => "Use M374 to save the height map"
                });

            default:
                return new Message(MessageType.Error, $"Invalid S parameter {sValue}");
        }
    }

    /// <summary>Macro that runs in place of G29 when the machine has one</summary>
    private const string MeshMacro = "mesh.g";

    /// <summary>
    /// Probe every reachable point of the grid and adopt the map that comes out
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// The points are walked in a serpentine so that the head does not fly back across the bed
    /// between rows, which is what RepRapFirmware does. Points outside a circular grid's radius are
    /// skipped and then filled in from the ones that were probed, because the interpolation needs all
    /// four corners of whichever cell it lands in
    /// </remarks>
    private async ValueTask<Message> ProbeGridAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        int probeNumber = code.GetInt('K', 0);
        if (probeNumber < 0 || probeNumber >= RemoteProbes.MaxProbes)
        {
            return new Message(MessageType.Error, $"Z probe number out of range (0..{RemoteProbes.MaxProbes - 1})");
        }

        if (!await planner.WaitForStandstillAsync(cancellationToken))
        {
            throw new OperationCanceledException();
        }

        ProbeSettings settings = await ReadProbeSettingsAsync(probeNumber, cancellationToken);
        if (settings.Refusal is not null)
        {
            return new Message(MessageType.Error, settings.Refusal);
        }

        HeightMap map;
        int axis0, axis1;
        float[] probeOffsets;

        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            for (int axis = 0; axis < model.Move.Axes.Count; axis++)
            {
                if (!model.Move.Axes[axis].Homed)
                {
                    return new Message(MessageType.Error, "Must home the machine before bed probing");
                }
            }

            ProbeGrid grid = model.Move.Compensation.ProbeGrid;
            map = HeightMap.Over([grid.Axes[0], grid.Axes[1]],
                                 [grid.Mins[0], grid.Mins[1]],
                                 [grid.Maxs[0], grid.Maxs[1]],
                                 [grid.Spacings[0], grid.Spacings[1]],
                                 grid.Radius);
            if (!map.IsValid)
            {
                return new Message(MessageType.Error, "No valid grid defined for bed probing");
            }

            axis0 = AxisWithLetter(model.Move, grid.Axes[0]);
            axis1 = AxisWithLetter(model.Move, grid.Axes[1]);
            if (axis0 < 0 || axis1 < 0)
            {
                return new Message(MessageType.Error, "The grid names an axis the machine does not have");
            }

            // The probe sits away from the nozzle, so the head goes to the point minus the offset in
            // order to put the probe over it
            Probe probe = model.Sensors.Probes[probeNumber]!;
            probeOffsets =
            [
                axis0 < probe.Offsets.Count ? probe.Offsets[axis0] : 0.0f,
                axis1 < probe.Offsets.Count ? probe.Offsets[axis1] : 0.0f
            ];
        }

        // Whatever was being applied would offset every probing move by the shape of the old bed
        await bedCompensation.ClearAsync(cancellationToken);
        await macroRunner.TryRunAsync(code.Channel, $"deployprobe{probeNumber}.g", code, cancellationToken: cancellationToken);

        try
        {
            for (int index1 = 0; index1 < map.Nums[1]; index1++)
            {
                for (int step = 0; step < map.Nums[0]; step++)
                {
                    // Odd rows are walked backwards so the head carries on from where it was
                    int index0 = (index1 & 1) == 0 ? step : map.Nums[0] - 1 - step;
                    if (!map.CanProbePoint(index0, index1))
                    {
                        continue;
                    }

                    (float coord0, float coord1) = map.GetCoordinates(index0, index1);
                    await MoveToPointAsync(axis0, coord0 - probeOffsets[0], axis1, coord1 - probeOffsets[1],
                                           settings, cancellationToken);

                    (float? stopped, Message? error) = await TapAsync(code, probeNumber, settings, 0.0f, cancellationToken);
                    if (error is not null)
                    {
                        return error;
                    }
                    map.SetHeight(index0, index1, stopped!.Value - settings.TriggerHeight);
                }
            }
        }
        finally
        {
            await macroRunner.TryRunAsync(code.Channel, $"retractprobe{probeNumber}.g", code, cancellationToken: cancellationToken);
        }

        map.ExtrapolateMissing();
        await bedCompensation.AdoptAsync(map, cancellationToken);

        (float mean, float deviation, float minError, float maxError) = map.GetStatistics();
        return new Message(MessageType.Success, string.Create(CultureInfo.InvariantCulture,
            $"{map.MeasuredPoints} points probed, min error {minError:F3}, max error {maxError:F3}, "
            + $"mean {mean:F3}, deviation {deviation:F3}"));
    }

    /// <summary>
    /// Move to a point on the bed at the probe's dive height
    /// </summary>
    /// <param name="axis0">First axis of the grid</param>
    /// <param name="coord0">Where to put it</param>
    /// <param name="axis1">Second axis of the grid</param>
    /// <param name="coord1">Where to put it</param>
    /// <param name="settings">What the probe is configured to do</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An awaitable task</returns>
    private async ValueTask MoveToPointAsync(int axis0, float coord0, int axis1, float coord1,
                                             ProbeSettings settings, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            MoveSubmitResult result;

            using (await model.AccessReadWriteAsync(cancellationToken))
            {
                RawMove move = new()
                {
                    FeedRateMmPerSec = settings.TravelSpeed,
                    LinearAxesMentioned = true
                };

                for (int axis = 0; axis < model.Move.Axes.Count; axis++)
                {
                    move.Coords[axis] = model.Move.Axes[axis].MachinePosition ?? 0.0f;
                }
                move.Coords[axis0] = coord0;
                move.Coords[axis1] = coord1;

                // Travelling at the dive height rather than wherever the last tap left the head, so
                // the nozzle clears the bed on the way across
                move.Coords[settings.ZAxis] = ProbeStartHeight(settings, 0);

                result = planner.QueueMove(move);
            }

            if (result is MoveSubmitResult.Queued or MoveSubmitResult.NoMovement or MoveSubmitResult.Rejected)
            {
                await planner.WaitForStandstillAsync(cancellationToken);
                return;
            }
            await Task.Delay(RingFullRetryDelay, cancellationToken);
        }
    }

    /// <summary>
    /// Which axis carries a letter
    /// </summary>
    /// <param name="move">The move model</param>
    /// <param name="letter">The letter</param>
    /// <returns>The axis index, or -1 if the machine has no such axis</returns>
    /// <remarks>The caller must hold the object model lock</remarks>
    private static int AxisWithLetter(Move move, char letter)
    {
        for (int axis = 0; axis < move.Axes.Count; axis++)
        {
            if (move.Axes[axis].Letter == letter)
            {
                return axis;
            }
        }
        return -1;
    }
}
