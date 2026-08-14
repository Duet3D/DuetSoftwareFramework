using System;
using System.Collections.Generic;
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
/// <param name="SharesSwitchesAcrossDrives">
/// Whether any armed axis' switches are watched by drives other than its own, because moving it
/// needs them. The move has to spread those switches over the drivers of the set so that all of them
/// end up watched, which is the one thing the native side needs telling
/// </param>
internal sealed record ArmedMove(
    IReadOnlyList<int> ArmedAxes,
    IReadOnlyList<int> AxesToHold,
    uint TriggeredAxes,
    bool ReduceAcceleration,
    bool SharesSwitchesAcrossDrives);

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
    /// <param name="geometry">The machine's geometry, which decides which action an endstop takes</param>
    /// <param name="numAxes">Number of axes that can be planned for</param>
    /// <param name="plans">What each axis the code named watches, in the order it named them</param>
    /// <param name="closedSwitches">Which switches of an axis' endstop are closed, as a bitmap</param>
    /// <param name="stopOnInput">Per-drive stop inputs to fill in, indexed by drive</param>
    /// <returns>What was decided</returns>
    /// <exception cref="GCodeException">
    /// An axis has an endstop a move cannot be stopped by, or was named alongside an axis whose
    /// endstop has to stop every drive
    /// </exception>
    /// <remarks>
    /// Only the axes the plans cover are armed, which is only the axes the code named. A homing move
    /// naming X and Y must not be stopped by Z's switch happening to be closed. What each of them
    /// watches was settled by <see cref="EndstopPlanner"/> before the boards were told about it, so
    /// this and the arming that went over the bus cannot disagree
    /// </remarks>
    public static ArmedMove Arm(Move move, KinematicsEngine geometry, int numAxes,
                                IReadOnlyList<EndstopPlan> plans, Func<int, uint> closedSwitches,
                                MoveStopInput[] stopOnInput)
    {
        List<int> armedAxes = [], alreadyClosed = [];
        bool reduceAcceleration = false;
        bool copiedAcrossASet = false;

        // Which axis has claimed each drive, so that a drive two axes would both have to stop is
        // caught rather than silently given to whichever was armed last
        int[] claimedBy = new int[MotionLimits.MaxAxesPlusExtruders];
        Array.Fill(claimedBy, -1);

        foreach (EndstopPlan plan in plans)
        {
            int axis = plan.Axis;
            Endstop endstop = plan.Endstop;
            IEndstopKind? kind = EndstopKinds.For(plan.Kind);

            // Refusing is the point. Leaving the axis unarmed and carrying on would run the move to
            // its full commanded length with nothing to stop it, which for a homing move means
            // driving into the end of the axis. RepRapFirmware's EnableAxisEndstops throws here for
            // the same reason
            // TODO if simulating continue to next axis
            if (kind is null)
            {
                throw new GCodeException(
                    $"Cannot home {move.Axes[axis].Letter}: its endstop type is not one a move can be stopped by");
            }
            if (kind.TryArm(plan, stopOnInput[axis]) is string reason)
            {
                throw new GCodeException($"Cannot home {move.Axes[axis].Letter}: {reason}");
            }

            reduceAcceleration |= kind.ReducesAcceleration;

            if (endstop.Triggered && !HoldClosedDrivers(geometry, closedSwitches, axis, stopOnInput[axis]))
            {
                alreadyClosed.Add(axis);
            }

            // Every drive that has to turn for this axis to move. Two axes whose sets overlap cannot
            // both be homed by one move: a drive carries one watch, so the second would overwrite
            // the first and leave one of the two endstops watched by nobody. Sets that do not
            // overlap are independent and may be homed together, however many of them there are
            uint controlling = geometry.GetControllingDrives(axis);
            for (int drive = 0; drive < claimedBy.Length; drive++)
            {
                if ((controlling & (1u << drive)) != 0 && claimedBy[drive] >= 0)
                {
                    throw new GCodeException(
                        $"Cannot home {move.Axes[claimedBy[drive]].Letter} and {move.Axes[axis].Letter} together: "
                        + $"both need drive logical axis {drive} to move, but a drive can only watch one endstop at a time");
                }
            }

            // Every drive of the set watches this axis' endstop under the one group, so whichever of
            // the axis' switches fires stops the set and nothing outside it. The action outranks
            // whatever the kind chose, as RepRapFirmware's GetResult tests the coupling before it
            // tests individualMotors: moving one motor of a coupled axis is not something the
            // kinematics can express.
            //
            // All of the switches are kept, not just the first. RepRapFirmware watches every port of
            // an endstop whatever the action - PrimeAxis primes portsLeftToTrigger with all of them
            // and CheckTriggered scans them all
            if (NeedsEveryDrive(geometry, axis))
            {
                stopOnInput[axis].Action = StopAction.Group;
                copiedAcrossASet = true;
            }
            stopOnInput[axis].Group = (byte)axis;

            for (int drive = 0; drive < claimedBy.Length; drive++)
            {
                if ((controlling & (1u << drive)) == 0)
                {
                    continue;
                }
                claimedBy[drive] = axis;
                if (drive != axis && drive < stopOnInput.Length)
                {
                    stopOnInput[drive].CopyFrom(stopOnInput[axis]);
                }
            }

            armedAxes.Add(axis);
        }

        return new ArmedMove(armedAxes, alreadyClosed, AsBitmap(alreadyClosed), reduceAcceleration,
                             copiedAcrossASet);
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
