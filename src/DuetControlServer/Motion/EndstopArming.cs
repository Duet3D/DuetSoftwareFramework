using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion.Kinematics;
using DuetControlServer.Motion.Native;

namespace DuetControlServer.Motion;

/// <summary>
/// What arming a <c>G1 H</c> move decided, for the caller to act on
/// </summary>
/// <param name="ArmedAxes">Axes this move watches an endstop for, in the order they were named</param>
/// <param name="AxesToHold">
/// Axes to command to stay where they are, because their endstop is already closed and nothing on
/// them can usefully move
/// </param>
/// <param name="TriggeredAxes">
/// Axes whose endstop was already closed and which therefore count as having triggered, as a bitmap.
/// A move that holds an axis still has to conclude as though the switch fired, because it did - the
/// axis is at it
/// </param>
/// <param name="ReduceAcceleration">
/// Whether any armed endstop is a stall, which has to be approached slowly enough to tell a stall
/// from normal load. M201.1 configures the limit
/// </param>
internal sealed record ArmedMove(
    IReadOnlyList<int> ArmedAxes,
    IReadOnlyList<int> AxesToHold,
    uint TriggeredAxes,
    bool ReduceAcceleration);

/// <summary>
/// Deciding what a <c>G1 H</c> move watches, and what it must not move
/// </summary>
/// <remarks>
/// <para>
/// RepRapFirmware's <c>EndstopsManager::EnableAxisEndstops</c> and <c>SwitchEndstop::PrimeAxis</c>.
/// There it is a method on an object per endstop, which can hold the decision in its own fields;
/// here the decision has to be written into the move, because the component that acts on it is two
/// programs away and gets nothing but the move. So this reads the machine and writes
/// <see cref="RawMove.StopOnInput"/>, and every rule about what stops what lives in this one file.
/// </para>
/// <para>
/// It takes what it needs as arguments rather than reading them from services. The four stop actions
/// and the already-closed rule are the parts of the endstop path that have been wrong in practice,
/// and this is what lets them be tested against a machine description rather than against a running
/// printer
/// </para>
/// </remarks>
internal static class EndstopArming
{
    /// <summary>
    /// Work out what this move watches, and fill in the stop input of every drive
    /// </summary>
    /// <param name="move">The move subsystem of the object model, for the axes and their drivers</param>
    /// <param name="sensors">The sensors subsystem, for the endstops and probes</param>
    /// <param name="geometry">The machine's geometry, which decides which action an endstop takes</param>
    /// <param name="numAxes">Number of axes that can be planned for</param>
    /// <param name="axesMentioned">Axes the code named, in order</param>
    /// <param name="closedSwitches">Which switches of an axis' endstop are closed, as a bitmap</param>
    /// <param name="stopOnInput">Per-drive stop inputs to fill in, indexed by drive</param>
    /// <returns>What was decided</returns>
    /// <exception cref="GCodeException">
    /// An axis has no endstop, has one a move cannot be stopped by, or was named alongside an axis
    /// whose endstop has to stop every drive
    /// </exception>
    /// <remarks>
    /// Only the axes the code names are armed. A homing move naming X and Y must not be stopped by
    /// Z's switch happening to be closed
    /// </remarks>
    public static ArmedMove Arm(Move move, Sensors sensors, KinematicsEngine geometry, int numAxes,
                                IReadOnlyList<int> axesMentioned, Func<int, uint> closedSwitches,
                                MoveStopInput[] stopOnInput)
    {
        List<int> armedAxes = [], alreadyClosed = [];
        bool reduceAcceleration = false;

        // The axis whose endstop has to stop every drive, if the geometry has one in this move
        int stopAllAxis = -1;
        MoveStopInput stopAllInput = new();
        int independentlyArmed = 0;

        foreach (int axis in axesMentioned)
        {
            Endstop? endstop = axis < sensors.Endstops.Count ? sensors.Endstops[axis] : null;
            if (endstop is null)
            {
                throw new GCodeException($"No endstop configured for axis {move.Axes[axis].Letter}");
            }

            // TODO if simulating continue to next axis
            if (TryArmAxis(move, sensors, geometry, endstop, axis, stopOnInput[axis]) is string reason)
            {
                // Refusing is the point. Leaving the axis unarmed and carrying on would run the move
                // to its full commanded length with nothing to stop it, which for a homing move means
                // driving into the end of the axis. RepRapFirmware's EnableAxisEndstops throws here
                // for the same reason
                throw new GCodeException($"Cannot home {move.Axes[axis].Letter}: {reason}");
            }

            reduceAcceleration |= endstop.Type is EndstopType.MotorStallAny or EndstopType.MotorStallIndividual;

            if (endstop.Triggered && !HoldClosedDrivers(geometry, closedSwitches, axis, stopOnInput[axis]))
            {
                alreadyClosed.Add(axis);
            }

            if (NeedsEveryDrive(geometry, axis))
            {
                if (stopAllAxis >= 0)
                {
                    throw new GCodeException(
                        $"Cannot home {move.Axes[stopAllAxis].Letter} and {move.Axes[axis].Letter} together: "
                        + "on this kinematics either endstop has to stop every drive");
                }
                stopAllAxis = axis;
                stopAllInput.CopyFrom(stopOnInput[axis]);
            }
            else
            {
                independentlyArmed++;
            }
            armedAxes.Add(axis);
        }

        if (stopAllAxis < 0)
        {
            return new ArmedMove(armedAxes, alreadyClosed, AsBitmap(alreadyClosed), reduceAcceleration);
        }

        if (independentlyArmed > 0)
        {
            throw new GCodeException(
                $"Cannot home {move.Axes[stopAllAxis].Letter} with another axis: "
                + "its endstop has to stop every drive, which would disarm the others");
        }

        // Every drive watches the one switch, so whichever driver sees the change first, they all
        // stop. That is what makes this stopAll rather than stopAxis. A per-driver endstop is
        // demoted to its first switch here for the same reason RepRapFirmware demotes it: the drives
        // are coupled, so letting each motor wait for its own switch would keep the others running
        for (int drive = 0; drive < stopOnInput.Length; drive++)
        {
            stopOnInput[drive].SetShared(stopAllInput.Handle, stopAllInput.Boards[0]);
        }

        if (!alreadyClosed.Contains(stopAllAxis))
        {
            return new ArmedMove(armedAxes, alreadyClosed, AsBitmap(alreadyClosed), reduceAcceleration);
        }

        // On coupled kinematics the whole move stops on the one endstop, so an endstop that is
        // already closed holds every drive rather than only its own axis
        List<int> everyAxis = [];
        for (int axis = 0; axis < numAxes; axis++)
        {
            everyAxis.Add(axis);
        }
        return new ArmedMove(armedAxes, everyAxis, AsBitmap(alreadyClosed), reduceAcceleration);
    }

    /// <summary>
    /// Whether moving an axis needs drives other than its own, so it cannot be stopped by itself
    /// </summary>
    /// <param name="geometry">The machine's geometry</param>
    /// <param name="axis">The axis</param>
    /// <returns>True if its endstop has to stop every drive in the move</returns>
    /// <remarks>
    /// RepRapFirmware's test in <c>SwitchEndstop::PrimeAxis</c>. On a CoreXY, holding X still needs
    /// both motors, so stopping only "X's drivers" would leave the other running and drag the head
    /// diagonally into the switch
    /// </remarks>
    private static bool NeedsEveryDrive(KinematicsEngine geometry, int axis)
        => (geometry.GetControllingDrives(axis) & ~(1u << axis)) != 0;

    /// <summary>
    /// Fill in what stops one axis, whatever kind of endstop it has
    /// </summary>
    /// <param name="move">The move subsystem of the object model</param>
    /// <param name="sensors">The sensors subsystem</param>
    /// <param name="geometry">The machine's geometry</param>
    /// <param name="endstop">The axis' endstop</param>
    /// <param name="axis">Axis number</param>
    /// <param name="stopInput">The stop input to fill in</param>
    /// <returns>Null if the axis is armed, else why its endstop cannot stop a move</returns>
    /// <remarks>
    /// The four kinds are RepRapFirmware's four endstop classes. A switch and a Z probe are inputs a
    /// board watches, so they name a handle the board already knows; a stall is detected by the
    /// driver itself, so what the move watches is the board's stall report. The reason travels back
    /// with the refusal rather than being derived again from the type, because the two would then
    /// have to be kept in step and only one of them is exercised
    /// </remarks>
    private static string? TryArmAxis(Move move, Sensors sensors, KinematicsEngine geometry,
                                      Endstop endstop, int axis, MoveStopInput stopInput)
    {
        switch (endstop.Type)
        {
            case EndstopType.InputPin:
                // CHECK should we send a CanMessageChangeInputMonitorV1 message to actually enable the endstops like in `SwitchEndstop::PrimeAxis()`?
                return RemoteEndstops.TryGetStopInput(endstop, axis, move.Axes[axis].Drivers.Count, stopInput)
                       ? null
                       : "its endstop has no port assigned";

            case EndstopType.ZProbeAsEndstop:
                {
                    int probeNumber = endstop.Probe ?? 0;
                    Probe? probe = probeNumber < sensors.Probes.Count ? sensors.Probes[probeNumber] : null;
                    return probe is not null && RemoteProbes.TryGetStopInput(probe, probeNumber, stopInput)
                           ? null
                           : "its endstop is a Z probe that cannot stop a move; check M558";
                }

            case EndstopType.MotorStallAny:
            case EndstopType.MotorStallIndividual:
                {
                    // Which drivers to watch is the geometry's answer, not the axis': stopping on a
                    // CoreXY's X stall means watching both motors, because moving X turns both
                    uint drives = geometry.GetControllingDrives(axis);
                    List<DuetAPI.Utility.DriverId> drivers = [];
                    for (int drive = 0; drive < move.Axes.Count && drive < MotionLimits.MaxAxes; drive++)
                    {
                        if ((drives & (1u << drive)) != 0)
                        {
                            drivers.AddRange(move.Axes[drive].Drivers);
                        }
                    }
                    return RemoteEndstops.TryGetStallStopInput(CollectionsMarshal.AsSpan(drivers), stopInput)
                           ? null
                           : "no driver is assigned to it";
                }

            default:
                return "its endstop type is not one a move can be stopped by";
        }
    }

    /// <summary>
    /// Hold only the motors of an axis that are already on their own switch
    /// </summary>
    /// <param name="geometry">The machine's geometry</param>
    /// <param name="closedSwitches">Which switches of an axis' endstop are closed</param>
    /// <param name="axis">The axis</param>
    /// <param name="stopInput">Its stop input, already filled in</param>
    /// <returns>True if the axis still has a motor to move, so it must not be held as a whole</returns>
    /// <remarks>
    /// <para>
    /// Only an axis with a switch per driver can answer yes. That arrangement exists to square a
    /// gantry - each motor runs on to its own switch, so a skewed gantry ends up straight - and the
    /// move that corrects a skew is precisely the one that starts with one side already down.
    /// Holding the whole axis because one switch is closed would make it do nothing, leaving the
    /// gantry skewed and the axis reporting itself homed.
    /// </para>
    /// <para>
    /// RepRapFirmware reaches the same place from <c>DDA::Prepare</c>: <c>CheckEndstops(false)</c>
    /// runs after the per-driver movements have been accumulated and before they are sent, and
    /// <c>StopDriverWhenProvisional</c> zeroes the steps of, in its own words, "the motors
    /// concerned". Its <c>SwitchEndstop::CheckTriggered</c> only escalates to stopping the axis once
    /// one switch is left, which is the same rule as returning false here when every switch is
    /// closed
    /// </para>
    /// </remarks>
    private static bool HoldClosedDrivers(KinematicsEngine geometry, Func<int, uint> closedSwitches,
                                          int axis, MoveStopInput stopInput)
    {
        if (stopInput.NumSwitches < 2)
        {
            return false;                       // one switch stops every driver, so none can run on
        }

        // stopAll outranks stopDriver, and the demotion to it happens after this. Moving one motor
        // of a coupled axis is not a thing the kinematics can express: the drives that would have to
        // move to hold the others still are the ones being held
        if (NeedsEveryDrive(geometry, axis))
        {
            return false;
        }

        uint closed = closedSwitches(axis);
        uint all = (1u << stopInput.NumSwitches) - 1;
        if ((closed & all) == all)
        {
            return false;                       // every motor is down; there is nothing left to move
        }

        for (int switchIndex = 0; switchIndex < stopInput.NumSwitches; switchIndex++)
        {
            if ((closed & (1u << switchIndex)) != 0)
            {
                stopInput.HoldDriver(switchIndex);
            }
        }
        return true;
    }

    /// <summary>
    /// Turn a list of axes into the bitmap the interpreter's latch is kept as
    /// </summary>
    /// <param name="axes">The axes</param>
    /// <returns>The bitmap</returns>
    private static uint AsBitmap(IReadOnlyList<int> axes)
    {
        uint bitmap = 0;
        foreach (int axis in axes)
        {
            if (axis < MotionLimits.MaxAxes)
            {
                bitmap |= 1u << axis;
            }
        }
        return bitmap;
    }
}
