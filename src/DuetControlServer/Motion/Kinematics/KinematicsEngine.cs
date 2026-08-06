using System;
using DuetControlServer.Link.Native;
using DuetControlServer.Motion.Native;

namespace DuetControlServer.Motion.Kinematics;

/// <summary>
/// The speed and acceleration a move has been limited to so far
/// </summary>
/// <remarks>
/// Passed by reference through the planning steps that may lower it. Only ever lowered, never
/// raised: each step knows a reason the move cannot go faster, and the move ends up at the smallest
/// of them
/// </remarks>
internal struct MoveLimits
{
    /// <summary>Speed the move may run at, in mm per step clock</summary>
    public float RequestedSpeed;

    /// <summary>Acceleration and deceleration limit, in mm per step clock squared</summary>
    public float MaxAcceleration;

    /// <summary>
    /// Lower the limits to the given values if they are more restrictive
    /// </summary>
    /// <param name="maxSpeed">Speed limit to apply</param>
    /// <param name="maxAcceleration">Acceleration limit to apply</param>
    public void Limit(float maxSpeed, float maxAcceleration)
    {
        if (maxSpeed < RequestedSpeed)
        {
            RequestedSpeed = maxSpeed;
        }
        if (maxAcceleration < MaxAcceleration)
        {
            MaxAcceleration = maxAcceleration;
        }
    }
}

/// <summary>
/// The move a geometry is being asked to limit, in the terms the geometry needs to reason about it
/// </summary>
/// <remarks>
/// RepRapFirmware hands its <c>Kinematics::LimitSpeedAndAcceleration</c> the whole DDA, and the polar
/// geometry reaches into it for the turntable's motor movement and the length of the move. Only those
/// few fields are actually read, so this passes them rather than the whole builder
/// </remarks>
internal readonly ref struct PlannedMove
{
    /// <summary>Unit direction vector in the positive hyperquadrant, in axis space</summary>
    public ReadOnlySpan<float> NormalisedDirectionVector { get; init; }

    /// <summary>Motor position the move starts from, in microsteps</summary>
    public ReadOnlySpan<int> StartMotorPos { get; init; }

    /// <summary>Motor position the move ends at, in microsteps</summary>
    public ReadOnlySpan<int> EndMotorPos { get; init; }

    /// <summary>Microsteps per mm - or per degree on a rotary geometry - for each drive</summary>
    public ReadOnlySpan<float> StepsPerMm { get; init; }

    /// <summary>Axes the user can refer to</summary>
    public int NumVisibleAxes { get; init; }

    /// <summary>How far the move goes, in mm</summary>
    public float TotalDistance { get; init; }

    /// <summary>Whether a continuous rotation axis may take the short way round</summary>
    public bool ContinuousRotationShortcut { get; init; }
}

/// <summary>
/// Which movement a geometry needs broken into segments
/// </summary>
/// <remarks>
/// Ported from RepRapFirmware's <c>SegmentationType</c> bitfield. The distinctions are not
/// decoration: a geometry whose Z is linear does not need Z movement counted towards the segment
/// length, and one whose travel moves bow as badly as its printing moves has to segment those too
/// </remarks>
[Flags]
internal enum SegmentationType : byte
{
    /// <summary>The transform is linear, so a straight line stays straight</summary>
    None = 0,

    /// <summary>Straight moves have to be approximated by short ones</summary>
    Segment = 1,

    /// <summary>Z movement counts towards how long the move is, because Z is not independent either</summary>
    IncludeZ = 2,

    /// <summary>Uncoordinated moves are segmented as well, not only printing ones</summary>
    IncludeG0 = 4
}

/// <summary>
/// What limiting a target position came to
/// </summary>
/// <remarks>
/// Ported from RepRapFirmware's <c>LimitPositionResult</c>. The two "intermediate" cases are the
/// interesting ones: a straight move between two reachable points can still pass through somewhere
/// the machine cannot go, which is what a delta's towers and a SCARA's inner radius both do
/// </remarks>
internal enum LimitPositionResult : byte
{
    /// <summary>The move is possible as asked, all the way along</summary>
    Ok,

    /// <summary>The end was out of reach and has been brought in; the path to it is fine</summary>
    Adjusted,

    /// <summary>The end is reachable but the straight line to it is not</summary>
    IntermediateUnreachable,

    /// <summary>The end had to be brought in and the line to it is still not reachable</summary>
    AdjustedAndIntermediateUnreachable
}

/// <summary>
/// Machine geometry: how a position in axis space maps onto the motors that have to turn to reach it
/// </summary>
/// <remarks>
/// <para>
/// Ported from RepRapFirmware's <c>Kinematics</c> class hierarchy. This is the part of the motion
/// engine that knows what shape the machine is. Everything upstream of it works in axis coordinates -
/// what the user typed - and everything downstream works in motor microsteps; this is the boundary
/// between the two.
/// </para>
/// <para>
/// It lives on this side of the split because the native engine no longer has it: the planner there
/// takes endpoints that have already been through the kinematics, and asks for the two derived
/// answers it still needs (see <c>MotionConfig</c>'s continuousRotationAxes and controllingDrives)
/// rather than recomputing them
/// </para>
/// </remarks>
internal abstract class KinematicsEngine
{
    /// <summary>
    /// Name of this geometry, for diagnostics and the object model
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// Convert a position in axis space to motor microsteps
    /// </summary>
    /// <param name="machinePos">Axis coordinates in mm</param>
    /// <param name="stepsPerMm">Microsteps per mm for each drive</param>
    /// <param name="numVisibleAxes">Axes the user can refer to</param>
    /// <param name="numTotalAxes">Axes in total, including any the kinematics adds</param>
    /// <param name="motorPos">Filled in with the motor position in microsteps</param>
    /// <param name="isCoordinated">Whether this is a coordinated move rather than setting a position</param>
    /// <returns>Ok, or why the position could not be reached</returns>
    /// <remarks>
    /// <para>
    /// A motor that no visible axis affects keeps whatever <paramref name="motorPos"/> already held,
    /// which is how the caller carries an untouched drive forward from the previous move
    /// </para>
    /// <para>
    /// <paramref name="isCoordinated"/> matters only where a position is reachable in more than one
    /// pose - a SCARA arm can fold either way to the same point. Changing pose is a move of its own,
    /// so a coordinated move that would need one is refused rather than silently taken
    /// </para>
    /// </remarks>
    public abstract NativeMovementError CartesianToMotorSteps(
        ReadOnlySpan<float> machinePos,
        ReadOnlySpan<float> stepsPerMm,
        int numVisibleAxes,
        int numTotalAxes,
        Span<int> motorPos,
        bool isCoordinated = false);

    /// <summary>
    /// Convert motor microsteps back to a position in axis space
    /// </summary>
    /// <param name="motorPos">Motor position in microsteps</param>
    /// <param name="stepsPerMm">Microsteps per mm for each drive</param>
    /// <param name="numVisibleAxes">Axes the user can refer to</param>
    /// <param name="numTotalAxes">Axes in total</param>
    /// <param name="machinePos">Filled in with the axis coordinates in mm</param>
    /// <remarks>Used after homing and after a move that was cut short</remarks>
    public abstract void MotorStepsToCartesian(
        ReadOnlySpan<int> motorPos,
        ReadOnlySpan<float> stepsPerMm,
        int numVisibleAxes,
        int numTotalAxes,
        Span<float> machinePos);

    /// <summary>
    /// Further restrict a move's speed and acceleration to what the mechanics can take
    /// </summary>
    /// <param name="limits">Limits to lower</param>
    /// <param name="move">The move being planned</param>
    /// <param name="maxFeedrates">Per-drive maximum speed, mm per step clock</param>
    /// <param name="accelerations">Per-drive maximum acceleration, mm per step clock squared</param>
    /// <remarks>
    /// <para>
    /// The per-axis limits have already been applied by the caller. What is left is the constraint
    /// this geometry adds: on a Cartesian machine X and Y may be limited independently, so a diagonal
    /// move is allowed to be faster than either; on anything where the two are coupled, it is not.
    /// </para>
    /// <para>
    /// This default is RepRapFirmware's <c>Kinematics::LimitSpeedAndAcceleration</c>: hold the
    /// combined XY speed down to the lower of the two axis limits. It suits every geometry whose X
    /// and Y motors do not move independently, which is all of them except the matrix-driven ones -
    /// and those override it
    /// </para>
    /// </remarks>
    public virtual void LimitSpeedAndAcceleration(
        ref MoveLimits limits,
        in PlannedMove move,
        ReadOnlySpan<float> maxFeedrates,
        ReadOnlySpan<float> accelerations)
    {
        if (move.NormalisedDirectionVector.Length < 2 || maxFeedrates.Length < 2 || accelerations.Length < 2)
        {
            return;
        }

        float dx = move.NormalisedDirectionVector[0], dy = move.NormalisedDirectionVector[1];
        float xySum = dx + dy;
        if (xySum > 0.05f)
        {
            // Interpolate between the X and Y limits by how much of the move each contributes, then
            // scale by the length of the XY part: a move that is mostly Z has little XY in it and so
            // is barely restricted by what X and Y can do
            float maxSpeedTimesXySum = (maxFeedrates[0] * dx) + (maxFeedrates[1] * dy);
            float maxAccelerationTimesXySum = (accelerations[0] * dx) + (accelerations[1] * dy);
            float xyFactor = xySum * MathF.Sqrt((dx * dx) + (dy * dy));
            limits.Limit(maxSpeedTimesXySum / xyFactor, maxAccelerationTimesXySum / xyFactor);
        }
    }

    /// <summary>
    /// Which drives have to be energised to hold the given axis in place
    /// </summary>
    /// <param name="axis">Axis number</param>
    /// <returns>Logical drive bitmap</returns>
    /// <remarks>
    /// Just the corresponding motor on a Cartesian machine. On CoreXY holding X still requires both
    /// motors, because either one turning alone would move it. The native planner needs this to
    /// decide which drivers to enable for a move, which is why it is pushed down in MotionConfig
    /// </remarks>
    public virtual uint GetControllingDrives(int axis) => axis >= 0 ? 1u << axis : 0u;

    /// <summary>
    /// Bed tilt correction to apply to Z for a unit of movement along the given axis
    /// </summary>
    /// <param name="axis">Axis number</param>
    /// <returns>Correction factor, zero if this geometry has none</returns>
    public virtual float GetTiltCorrection(int axis) => 0.0f;

    /// <summary>
    /// Axes that wrap at 360 degrees, so a move may take the short way round
    /// </summary>
    public virtual uint ContinuousRotationAxes => 0;

    /// <summary>
    /// Whether homing this geometry moves axes or individual motors
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>HomingMode</c>. False - <c>homeCartesianAxes</c> - means an endstop belongs
    /// to an axis and a homing move is an ordinary move through the kinematics; true -
    /// <c>homeIndividualDrives</c> - means an endstop belongs to a motor, as on a delta where each
    /// tower has its own switch, and a homing move has to address the motors directly
    /// </remarks>
    public virtual bool HomesIndividualDrives => false;

    /// <summary>
    /// Whether a move of the given type addresses the motors directly rather than the axes
    /// </summary>
    /// <param name="moveType">What kind of move the H parameter asked for</param>
    /// <returns>True if the coordinates are per-motor rather than per-axis</returns>
    /// <remarks>
    /// Ported from <c>Move::IsRawMotorMove</c>. H2 always is, by definition. Every other special move
    /// is one only where the geometry homes individual drives, because on such a geometry there is no
    /// axis for the endstop to belong to - which is why the same <c>G1 H1</c> means different things
    /// on a CoreXY and on a delta
    /// </remarks>
    public bool IsRawMotorMove(MoveType moveType)
        => moveType == MoveType.RawMotor || (moveType != MoveType.Normal && HomesIndividualDrives);

    /// <summary>
    /// Whether this geometry needs a straight move broken into short ones, and along which axes
    /// </summary>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>SegmentationType</c>. A geometry that maps axis space onto its motors
    /// non-linearly cannot draw a straight line by transforming the two ends of one: the motors would
    /// interpolate linearly between motor positions, and the head would bow. Chopping the move into
    /// pieces short enough that the bow is smaller than a step is how every such machine does it.
    /// </para>
    /// <para>
    /// A Cartesian machine needs none of this, because the transform is the identity and a straight
    /// line in motor space already is one
    /// </para>
    /// </remarks>
    public virtual SegmentationType Segmentation => SegmentationType.None;

    /// <summary>
    /// How many segments per second of movement this geometry wants
    /// </summary>
    /// <remarks>RepRapFirmware's <c>DefaultSegmentsPerSecond</c>, configurable there by M669</remarks>
    public virtual float SegmentsPerSecond => 100.0f;

    /// <summary>
    /// Shortest segment worth producing, mm
    /// </summary>
    /// <remarks>
    /// The other half of the pair: a slow move would otherwise be cut into far more pieces than the
    /// error justifies, and each one costs a transform and a submission
    /// </remarks>
    public virtual float MinSegmentLength => 0.2f;

    /// <summary>
    /// Which axes have to be homed before a move may touch the given ones
    /// </summary>
    /// <param name="axesMoving">Axes the move wants to touch, as a bitmap</param>
    /// <param name="disallowMovesBeforeHoming">Whether M564 forbids moving an unhomed axis at all</param>
    /// <returns>Axes that must be homed first, as a bitmap</returns>
    /// <remarks>
    /// RepRapFirmware's <c>Kinematics::MustBeHomedAxes</c>. Where an axis is driven on its own, this
    /// is M564's answer and nothing more. Where the geometry couples them it is not optional: on a
    /// delta the head's position is a function of all three towers, so moving in X alone is not a
    /// thing the machine can do, and none of it means anything until every tower is homed - which is
    /// why the coupled geometries widen the set regardless of what M564 says
    /// </remarks>
    public virtual uint MustBeHomedAxes(uint axesMoving, bool disallowMovesBeforeHoming)
        => disallowMovesBeforeHoming ? axesMoving : 0;

    /// <summary>
    /// Bring a target position within what the machine can reach
    /// </summary>
    /// <param name="finalCoords">Target position, adjusted in place if it is out of reach</param>
    /// <param name="initialCoords">
    /// Where the move starts, so that the path can be checked as well as its end; empty to check the
    /// end alone
    /// </param>
    /// <param name="numVisibleAxes">Number of axes to consider</param>
    /// <param name="axesToLimit">Axes that may be adjusted, as a bitmap</param>
    /// <param name="isCoordinated">Whether the axes move together, which decides what path is taken</param>
    /// <param name="applyM208Limits">Whether the configured axis minima and maxima apply</param>
    /// <returns>What had to be done to make the move possible</returns>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>Kinematics::LimitPosition</c>. The base implementation is the M208 box and
    /// nothing more, which is the whole answer for a Cartesian machine. Every other geometry has a
    /// reachable region that is not a box - a delta's is a cylinder capped by the towers, a polar's an
    /// annulus, a SCARA's the reach of two arms - and overrides this.
    /// </para>
    /// <para>
    /// <paramref name="initialCoords"/> is what separates "can it get there" from "can it get there
    /// in a straight line". On a delta the highest point of a straight move is not either end of it,
    /// so a move between two reachable points can pass through one that is not
    /// </para>
    /// </remarks>
    public virtual LimitPositionResult LimitPosition(Span<float> finalCoords, ReadOnlySpan<float> initialCoords,
                                                     int numVisibleAxes, uint axesToLimit, bool isCoordinated,
                                                     bool applyM208Limits)
        => applyM208Limits && LimitToAxisRange(finalCoords, 0, numVisibleAxes, axesToLimit)
            ? LimitPositionResult.Adjusted
            : LimitPositionResult.Ok;

    /// <summary>
    /// Clamp axes to their configured minimum and maximum
    /// </summary>
    /// <param name="coords">Coordinates, adjusted in place</param>
    /// <param name="firstAxis">First axis to consider, for geometries that handle the lower ones themselves</param>
    /// <param name="numVisibleAxes">Number of axes to consider</param>
    /// <param name="axesToLimit">Axes that may be adjusted, as a bitmap</param>
    /// <returns>True if anything was adjusted</returns>
    /// <remarks>
    /// RepRapFirmware's <c>LimitPositionFromAxis</c>, tolerance included. Homing converts an axis
    /// limit to motor steps and back again, and that round trip does not land exactly on the limit
    /// when the steps per mm is not a whole number. Without the tolerance an axis would be reported
    /// out of range the moment it was homed to its own maximum
    /// </remarks>
    protected bool LimitToAxisRange(Span<float> coords, int firstAxis, int numVisibleAxes, uint axesToLimit)
    {
        bool limited = false;
        for (int axis = firstAxis; axis < numVisibleAxes && axis < coords.Length; axis++)
        {
            if ((axesToLimit & (1u << axis)) == 0)
            {
                continue;
            }

            if (coords[axis] < AxisMinima[axis] - AxisRoundingError)
            {
                coords[axis] = AxisMinima[axis];
                limited = true;
            }
            else if (coords[axis] > AxisMaxima[axis] + AxisRoundingError)
            {
                coords[axis] = AxisMaxima[axis];
                limited = true;
            }
        }
        return limited;
    }

    /// <summary>
    /// How far outside its limit an axis may be before it counts as out of range, mm
    /// </summary>
    /// <remarks>RepRapFirmware's <c>AxisRoundingError</c></remarks>
    public const float AxisRoundingError = 0.02f;

    /// <summary>Configured minimum of each axis, mm</summary>
    public float[] AxisMinima { get; } = new float[MotionLimits.MaxAxes];

    /// <summary>Configured maximum of each axis, mm</summary>
    public float[] AxisMaxima { get; } = new float[MotionLimits.MaxAxes];

    /// <summary>
    /// Where a drive is when its endstop fires
    /// </summary>
    /// <param name="drive">Logical drive the endstop belongs to</param>
    /// <param name="highEnd">Whether the endstop is at the high end of travel</param>
    /// <param name="axisMin">The axis' configured minimum, for geometries where that is the answer</param>
    /// <param name="axisMax">The axis' configured maximum</param>
    /// <param name="endPoints">Current motor positions in microsteps, for geometries whose joints interact</param>
    /// <param name="stepsPerMm">Steps per mm per drive, to convert those</param>
    /// <returns>The position in mm, or degrees for a rotary joint</returns>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>Kinematics::GetEndstopPosition</c>. On a Cartesian machine the endstop is
    /// at the end of an axis, so the answer is the axis limit and this is trivial. On anything that
    /// homes individual drives it is not: a delta tower's switch is at a carriage height, a rotary
    /// delta's at an arm angle, a polar's at a radius. The value is a drive position, not an axis
    /// coordinate, and the axis coordinates are derived from it afterwards.
    /// </para>
    /// <para>
    /// <paramref name="endPoints"/> is here for SCARA, where turning one joint drags another: where
    /// the distal joint's switch sits depends on where the proximal joint already is. Every other
    /// geometry ignores it
    /// </para>
    /// </remarks>
    public virtual float GetEndstopPosition(int drive, bool highEnd, float axisMin, float axisMax,
                                            ReadOnlySpan<int> endPoints, ReadOnlySpan<float> stepsPerMm)
        => highEnd ? axisMax : axisMin;

    /// <summary>
    /// Macro that homes everything, as in RepRapFirmware's <c>HomeAllFileName</c>
    /// </summary>
    public const string HomeAllFile = "homeall.g";

    /// <summary>
    /// Which macro to run next to home some of a set of axes
    /// </summary>
    /// <param name="toBeHomed">Axes still to home, as a bitmap</param>
    /// <param name="alreadyHomed">Axes already homed, as a bitmap</param>
    /// <param name="axisLetters">Letter of each axis, in axis order</param>
    /// <param name="fileName">The macro to run, meaningless if any axes must be homed first</param>
    /// <returns>Axes that have to be homed before any of <paramref name="toBeHomed"/> can be</returns>
    /// <remarks>
    /// <para>
    /// Homing is a sequence of macros rather than one operation because only the machine's own
    /// configuration knows how to home it. The caller runs whichever macro comes back, sees which
    /// axes it homed, and asks again - so this only has to name the next step, not plan the whole
    /// sequence.
    /// </para>
    /// <para>
    /// Asking for every axis runs <c>homeall.g</c>; asking for some runs <c>home&lt;letter&gt;.g</c>
    /// for the lowest of them. The exception is a Z axis homed with a probe, which cannot be homed
    /// until the probe can be positioned over the bed - that is what the returned bitmap says
    /// </para>
    /// </remarks>
    public virtual uint GetHomingFileName(uint toBeHomed, uint alreadyHomed, ReadOnlySpan<char> axisLetters,
                                          out string fileName)
    {
        uint allAxes = axisLetters.Length >= 32 ? uint.MaxValue : (1u << axisLetters.Length) - 1;
        if ((toBeHomed & allAxes) == allAxes)
        {
            fileName = HomeAllFile;
            return 0;
        }

        // Homing Z with a probe means driving the nozzle at the bed, so it is only safe once the
        // probe can be put somewhere the bed actually is
        bool homeZLast = (toBeHomed & (1u << ZAxis)) != 0 && HomesZWithProbe;
        uint homeFirst = AxesToHomeBeforeProbing;

        for (int axis = 0; axis < axisLetters.Length; axis++)
        {
            if ((toBeHomed & (1u << axis)) == 0)
            {
                continue;
            }

            if (axis == ZAxis && homeZLast && (alreadyHomed & homeFirst) != homeFirst)
            {
                continue;
            }

            fileName = HomingFileFor(axisLetters[axis]);
            return 0;
        }

        // Nothing can be homed yet, which can only be the Z-with-a-probe case
        fileName = HomeAllFile;
        return homeFirst & ~alreadyHomed;
    }

    /// <summary>
    /// The macro that homes one axis
    /// </summary>
    /// <param name="letter">The axis letter</param>
    /// <returns>The macro name</returns>
    /// <remarks>
    /// A lower case axis letter is written with a leading apostrophe, as everywhere else in
    /// RepRapFirmware, so that <c>home'a.g</c> and <c>homea.g</c> are different files
    /// </remarks>
    protected static string HomingFileFor(char letter)
        => char.IsLower(letter) ? $"home'{letter}.g" : $"home{char.ToLowerInvariant(letter)}.g";

    /// <summary>
    /// Index of the Z axis, which is fixed in the kinematics even where the axis letters are not
    /// </summary>
    protected const int ZAxis = 2;

    /// <summary>
    /// Whether Z is homed by driving the nozzle at the bed until the probe triggers
    /// </summary>
    /// <remarks>
    /// Set by the caller from the Z endstop's type: RepRapFirmware treats an axis with no endstop as
    /// probing too, because a machine with no Z switch has nothing else to home with
    /// </remarks>
    public bool HomesZWithProbe { get; set; }

    /// <summary>
    /// Axes that must be homed before the bed can be probed
    /// </summary>
    /// <remarks>
    /// X and Y on most machines: the probe has to be somewhere over the bed before it is driven down.
    /// A delta has to home all three towers first, because none of its axes moves a motor of its own
    /// </remarks>
    public virtual uint AxesToHomeBeforeProbing => 0b011;

    /// <summary>
    /// Round a millimetre position to microsteps, reporting a position too far from the origin
    /// </summary>
    /// <param name="value">Position in microsteps as a real number</param>
    /// <param name="result">Rounded position</param>
    /// <returns>True if it fitted in an int</returns>
    /// <remarks>
    /// Endpoints are 32-bit microstep counts and moves are planned as differences between them, so a
    /// position that does not fit does not merely lose precision - it wraps, and the move that reads
    /// it commands the drive most of the way round the 32-bit range
    /// </remarks>
    protected static bool TryRoundToInt32(float value, out int result)
    {
        if (value > -2147483000.0f && value < 2147483000.0f)
        {
            result = (int)MathF.Round(value);
            return true;
        }
        result = 0;
        return false;
    }

    /// <summary>
    /// Convert the axes above the ones this geometry transforms, which each have a motor of their own
    /// </summary>
    /// <param name="machinePos">Axis coordinates in mm</param>
    /// <param name="stepsPerMm">Microsteps per mm for each drive</param>
    /// <param name="firstAxis">First axis to convert</param>
    /// <param name="numVisibleAxes">Axes the user can refer to</param>
    /// <param name="motorPos">Filled in with the motor position in microsteps</param>
    /// <returns>Ok, or MicrostepPositionTooLarge</returns>
    /// <remarks>
    /// Every non-Cartesian geometry describes the first two or three axes and leaves the rest alone,
    /// so they all end their forward transform with this loop
    /// </remarks>
    protected static NativeMovementError LinearAxesToMotorSteps(
        ReadOnlySpan<float> machinePos,
        ReadOnlySpan<float> stepsPerMm,
        int firstAxis,
        int numVisibleAxes,
        Span<int> motorPos)
    {
        NativeMovementError result = NativeMovementError.Ok;
        int limit = Math.Min(numVisibleAxes, Math.Min(machinePos.Length, Math.Min(stepsPerMm.Length, motorPos.Length)));

        for (int axis = firstAxis; axis < limit; axis++)
        {
            if (TryRoundToInt32(machinePos[axis] * stepsPerMm[axis], out int steps))
            {
                motorPos[axis] = steps;
            }
            else
            {
                result = NativeMovementError.MicrostepPositionTooLarge;
            }
        }
        return result;
    }

    /// <summary>
    /// Convert the axes above the ones this geometry transforms back to axis space
    /// </summary>
    /// <param name="motorPos">Motor position in microsteps</param>
    /// <param name="stepsPerMm">Microsteps per mm for each drive</param>
    /// <param name="firstAxis">First axis to convert</param>
    /// <param name="numVisibleAxes">Axes the user can refer to</param>
    /// <param name="machinePos">Filled in with the axis coordinates in mm</param>
    protected static void LinearMotorStepsToCartesian(
        ReadOnlySpan<int> motorPos,
        ReadOnlySpan<float> stepsPerMm,
        int firstAxis,
        int numVisibleAxes,
        Span<float> machinePos)
    {
        int limit = Math.Min(numVisibleAxes, Math.Min(machinePos.Length, Math.Min(stepsPerMm.Length, motorPos.Length)));
        for (int axis = firstAxis; axis < limit; axis++)
        {
            machinePos[axis] = motorPos[axis] / stepsPerMm[axis];
        }
    }

    /// <summary>
    /// The lowest <paramref name="count"/> drives, as a bitmap
    /// </summary>
    /// <param name="count">Number of drives</param>
    /// <returns>Logical drive bitmap</returns>
    protected static uint LowestDrives(int count) => count >= 32 ? uint.MaxValue : (1u << count) - 1;
}
