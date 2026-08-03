using System;
using DuetControlServer.Link.Native;

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
