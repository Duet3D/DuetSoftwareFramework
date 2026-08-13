using System;
using System.Collections.Generic;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion.Kinematics;

namespace DuetControlServer.Motion;

/// <summary>
/// One driver a move watches, and how fast it will be turning
/// </summary>
/// <param name="Driver">The driver</param>
/// <param name="StepsPerSecond">
/// Speed it will turn at. A stall is detected by comparing the back-EMF against what the commanded
/// speed implies, so the driver cannot detect one until it has been told this
/// </param>
internal readonly record struct WatchedDriver(DuetAPI.Utility.DriverId Driver, float StepsPerSecond);

/// <summary>
/// What one axis of a move watches, worked out once and read by both halves of arming it
/// </summary>
/// <param name="Axis">Axis number</param>
/// <param name="Kind">What kind of endstop it has</param>
/// <param name="Endstop">The endstop itself</param>
/// <param name="Probe">The probe standing in for it, if this is a Z probe endstop</param>
/// <param name="NumAxisDrivers">
/// How many drivers the axis itself has, which is what decides whether each motor gets its own switch
/// </param>
/// <param name="DriversWatched">
/// Every driver that has to turn for this axis to move, which on coupled kinematics is more than the
/// axis' own. Empty unless the endstop is a stall, because nothing else is watched per driver
/// </param>
internal sealed record EndstopPlan(
    int Axis,
    EndstopType Kind,
    Endstop Endstop,
    Probe? Probe,
    int NumAxisDrivers,
    IReadOnlyList<WatchedDriver> DriversWatched);

/// <summary>
/// Working out what a <c>G1 H</c> move watches, before anything acts on it
/// </summary>
/// <remarks>
/// <para>
/// Arming an endstop happens in two places and cannot happen in one. Telling a driver what speed to
/// expect is a CAN round trip, so it has to run before the move is built; writing the stop input into
/// the move has to run while it is being built, inside the planner lock, where nothing may await.
/// RepRapFirmware does both in one <c>PrimeAxis</c> because it has neither lock.
/// </para>
/// <para>
/// So the two phases stay separate and this is what keeps them agreeing: which drivers an axis
/// watches is derived here, once, and both phases are handed the answer. Deriving it twice is what
/// let the drivers a board was armed for drift from the drivers the move told the controller to watch
/// </para>
/// </remarks>
internal static class EndstopPlanner
{
    /// <summary>
    /// Work out what each axis the code names watches
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="move">The move subsystem of the object model</param>
    /// <param name="sensors">The sensors subsystem</param>
    /// <param name="geometry">The machine's geometry</param>
    /// <param name="numAxes">Number of axes that can be planned for</param>
    /// <param name="stepsPerMm">Microsteps per mm, by logical drive</param>
    /// <param name="feedRateMmPerSec">How fast the move will run</param>
    /// <returns>One plan per axis the code names, in the order it names them</returns>
    /// <exception cref="GCodeException">An axis the code names has no endstop</exception>
    /// <remarks>
    /// Only the axes the code names are planned for. A homing move naming X and Y must not be stopped
    /// by Z's switch happening to be closed
    /// </remarks>
    public static List<EndstopPlan> Plan(DuetAPI.Commands.Code code, Move move, Sensors sensors,
                                         KinematicsEngine geometry, int numAxes,
                                         IReadOnlyList<float> stepsPerMm, float feedRateMmPerSec)
    {
        List<EndstopPlan> plans = [];
        for (int axis = 0; axis < numAxes && axis < move.Axes.Count; axis++)
        {
            if (!code.HasParameter(move.Axes[axis].Letter))
            {
                continue;
            }

            Endstop? endstop = axis < sensors.Endstops.Count ? sensors.Endstops[axis] : null;
            if (endstop is null)
            {
                throw new GCodeException($"No endstop configured for axis {move.Axes[axis].Letter}");
            }

            plans.Add(new EndstopPlan(axis, endstop.Type, endstop, ProbeFor(sensors, endstop),
                                      move.Axes[axis].Drivers.Count,
                                      WatchedDrivers(move, geometry, numAxes, axis, endstop.Type,
                                                     stepsPerMm, feedRateMmPerSec)));
        }
        return plans;
    }

    /// <summary>
    /// The probe standing in for an endstop, if it is that kind
    /// </summary>
    /// <param name="sensors">The sensors subsystem</param>
    /// <param name="endstop">The endstop</param>
    /// <returns>The probe, or null if there is none or the endstop is not a probe</returns>
    private static Probe? ProbeFor(Sensors sensors, Endstop endstop)
    {
        if (endstop.Type != EndstopType.ZProbeAsEndstop)
        {
            return null;
        }

        int probeNumber = endstop.Probe ?? 0;
        return probeNumber < sensors.Probes.Count ? sensors.Probes[probeNumber] : null;
    }

    /// <summary>
    /// The drivers a stall-homed axis watches, and how fast each of them will turn
    /// </summary>
    /// <param name="move">The move subsystem of the object model</param>
    /// <param name="geometry">The machine's geometry</param>
    /// <param name="numAxes">Number of axes that can be planned for</param>
    /// <param name="axis">The axis</param>
    /// <param name="kind">What kind of endstop it has</param>
    /// <param name="stepsPerMm">Microsteps per mm, by logical drive</param>
    /// <param name="feedRateMmPerSec">How fast the move will run</param>
    /// <returns>The drivers, empty unless the endstop is a stall</returns>
    /// <remarks>
    /// Which drivers to watch is the geometry's answer, not the axis': stopping on a CoreXY's X stall
    /// means watching both motors, because moving X turns both. The speed is per drive rather than per
    /// axis because a coupled machine need not have the same steps per mm on each of them
    /// </remarks>
    private static IReadOnlyList<WatchedDriver> WatchedDrivers(Move move, KinematicsEngine geometry, int numAxes,
                                                               int axis, EndstopType kind,
                                                               IReadOnlyList<float> stepsPerMm, float feedRateMmPerSec)
    {
        if (kind is not (EndstopType.MotorStallAny or EndstopType.MotorStallIndividual))
        {
            return [];
        }

        List<WatchedDriver> drivers = [];
        uint drives = geometry.GetControllingDrives(axis);
        for (int drive = 0; drive < numAxes && drive < move.Axes.Count && drive < stepsPerMm.Count; drive++)
        {
            if ((drives & (1u << drive)) == 0)
            {
                continue;
            }

            // The driver is told steps per second, because that is what it compares against
            float stepsPerSecond = MathF.Abs(feedRateMmPerSec * stepsPerMm[drive]);
            foreach (DuetAPI.Utility.DriverId driver in move.Axes[drive].Drivers)
            {
                drivers.Add(new WatchedDriver(driver, stepsPerSecond));
            }
        }
        return drivers;
    }
}
