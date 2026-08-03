using System;
using DuetControlServer.Link.Native;

namespace DuetControlServer.Motion.Kinematics;

/// <summary>
/// The polar geometry: a radius arm over a turntable
/// </summary>
/// <remarks>
/// <para>
/// Ported from RepRapFirmware's <c>PolarKinematics</c>. The first drive moves the head in and out
/// along a radius and the second turns the bed under it, so a position is a radius and an angle rather
/// than an X and a Y. Steps per mm on the second drive are steps per degree.
/// </para>
/// <para>
/// The transform itself is just polar coordinates. What makes the geometry awkward is speed: near the
/// centre a small movement in X or Y is a large movement in angle, and the turntable cannot be spun
/// arbitrarily fast whatever the head is doing. That is what the extra speed limit is for, and it is
/// why the turntable carries its own maximum speed and acceleration separate from the axis limits
/// </para>
/// </remarks>
internal sealed class PolarKinematicsEngine : KinematicsEngine
{
    private const int RadiusDrive = 0, TurntableDrive = 1;
    private const int ZAxis = 2;

    private const float DegreesToRadians = MathF.PI / 180.0f;
    private const float RadiansToDegrees = 180.0f / MathF.PI;

    /// <summary>Closest the head may come to the centre of the turntable, mm</summary>
    public float MinRadius { get; }

    /// <summary>Furthest the head may go from the centre, mm</summary>
    public float MaxRadius { get; }

    /// <summary>Radius the head is at when homed, mm</summary>
    public float HomedRadius { get; }

    /// <summary>How fast the turntable may turn, degrees per step clock</summary>
    public float MaxTurntableSpeed { get; }

    /// <summary>How hard the turntable may be accelerated, degrees per step clock squared</summary>
    public float MaxTurntableAcceleration { get; }

    /// <inheritdoc />
    public override string Name => "Polar";

    /// <inheritdoc />
    /// <remarks>The turntable turns all the way round, so a move may take the short way there</remarks>
    public override uint ContinuousRotationAxes => 1u << TurntableDrive;

    /// <summary>Maximum radius RepRapFirmware assumes until M669 says otherwise, mm</summary>
    public const float DefaultMaxRadius = 150.0f;

    /// <summary>Turntable speed RepRapFirmware assumes until M669 says otherwise, degrees per second</summary>
    public const float DefaultMaxTurntableSpeed = 30.0f;

    /// <summary>Turntable acceleration RepRapFirmware assumes until M669 says otherwise, degrees per second squared</summary>
    public const float DefaultMaxTurntableAcceleration = 30.0f;

    /// <summary>
    /// Create a polar geometry
    /// </summary>
    /// <param name="minRadius">Closest the head may come to the centre, mm</param>
    /// <param name="maxRadius">Furthest the head may go from the centre, mm</param>
    /// <param name="homedRadius">Radius the head is at when homed, mm</param>
    /// <param name="maxTurntableSpeed">How fast the turntable may turn, degrees per step clock</param>
    /// <param name="maxTurntableAcceleration">How hard it may be accelerated, degrees per step clock squared</param>
    /// <remarks>
    /// The turntable limits are in step clock units rather than the object model's per-second ones,
    /// because that is what the move planner works in and converting once at configuration time is
    /// cheaper than converting on every move
    /// </remarks>
    public PolarKinematicsEngine(
        float minRadius,
        float maxRadius,
        float homedRadius,
        float maxTurntableSpeed,
        float maxTurntableAcceleration)
    {
        MinRadius = MathF.Max(minRadius, 0.0f);
        MaxRadius = maxRadius;
        HomedRadius = homedRadius;
        MaxTurntableSpeed = maxTurntableSpeed;
        MaxTurntableAcceleration = maxTurntableAcceleration;
    }

    /// <inheritdoc />
    public override NativeMovementError CartesianToMotorSteps(
        ReadOnlySpan<float> machinePos,
        ReadOnlySpan<float> stepsPerMm,
        int numVisibleAxes,
        int numTotalAxes,
        Span<int> motorPos,
        bool isCoordinated = false)
    {
        if (machinePos.Length < 2 || motorPos.Length < 2 || stepsPerMm.Length < 2)
        {
            return NativeMovementError.UnreachablePosition;
        }

        NativeMovementError result = NativeMovementError.Ok;

        float radius = MathF.Sqrt((machinePos[0] * machinePos[0]) + (machinePos[1] * machinePos[1]));
        if (TryRoundToInt32(radius * stepsPerMm[RadiusDrive], out int radiusSteps))
        {
            motorPos[RadiusDrive] = radiusSteps;
        }
        else
        {
            result = NativeMovementError.MicrostepPositionTooLarge;
        }

        if (motorPos[RadiusDrive] == 0)
        {
            // Dead centre: every angle puts the head in the same place, so turning the bed would be
            // movement for nothing. Leaving it at zero also keeps the angle defined rather than
            // whatever atan2 makes of a point at the origin
            motorPos[TurntableDrive] = 0;
        }
        else
        {
            float angle = MathF.Atan2(machinePos[1], machinePos[0]) * RadiansToDegrees;
            if (TryRoundToInt32(angle * stepsPerMm[TurntableDrive], out int angleSteps))
            {
                motorPos[TurntableDrive] = angleSteps;
            }
            else
            {
                result = NativeMovementError.MicrostepPositionTooLarge;
            }
        }

        NativeMovementError linearResult = LinearAxesToMotorSteps(machinePos, stepsPerMm, ZAxis, numVisibleAxes, motorPos);
        return result != NativeMovementError.Ok ? result : linearResult;
    }

    /// <inheritdoc />
    public override void MotorStepsToCartesian(
        ReadOnlySpan<int> motorPos,
        ReadOnlySpan<float> stepsPerMm,
        int numVisibleAxes,
        int numTotalAxes,
        Span<float> machinePos)
    {
        if (machinePos.Length < 2 || motorPos.Length < 2 || stepsPerMm.Length < 2)
        {
            return;
        }

        float angle = (motorPos[TurntableDrive] * DegreesToRadians) / stepsPerMm[TurntableDrive];
        float radius = motorPos[RadiusDrive] / stepsPerMm[RadiusDrive];
        machinePos[0] = radius * MathF.Cos(angle);
        machinePos[1] = radius * MathF.Sin(angle);

        LinearMotorStepsToCartesian(motorPos, stepsPerMm, ZAxis, numVisibleAxes, machinePos);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Deliberately not the base class's combined XY limit. On a polar machine the constraint that
    /// bites is the turntable, and how hard it bites depends on where on the bed the move is: the same
    /// arc near the centre is far more rotation than out at the rim
    /// </remarks>
    public override void LimitSpeedAndAcceleration(
        ref MoveLimits limits,
        in PlannedMove move,
        ReadOnlySpan<float> maxFeedrates,
        ReadOnlySpan<float> accelerations)
    {
        if (move.StartMotorPos.Length <= TurntableDrive || move.EndMotorPos.Length <= TurntableDrive
            || move.StepsPerMm.Length <= TurntableDrive)
        {
            return;
        }

        long turntableMovement = (long)move.EndMotorPos[TurntableDrive] - move.StartMotorPos[TurntableDrive];
        if (turntableMovement == 0)
        {
            return;
        }

        float stepsPerDegree = move.StepsPerMm[TurntableDrive];
        if (move.ContinuousRotationShortcut)
        {
            // Going more than half way round means the other way round is shorter, and that is the way
            // the machine will actually go
            long stepsPerRotation = (long)MathF.Round(360.0f * stepsPerDegree);
            if (turntableMovement > stepsPerRotation / 2)
            {
                turntableMovement -= stepsPerRotation;
            }
            else if (turntableMovement < -stepsPerRotation / 2)
            {
                turntableMovement += stepsPerRotation;
            }

            if (turntableMovement == 0)
            {
                return;
            }
        }

        // mm of movement per degree of rotation. The turntable's own limits are per degree, so this is
        // what turns them into limits on the move
        float stepRatio = move.TotalDistance * stepsPerDegree / Math.Abs(turntableMovement);
        limits.Limit(stepRatio * MaxTurntableSpeed, stepRatio * MaxTurntableAcceleration);
    }

    /// <inheritdoc />
    /// <remarks>Both motors move for either X or Y, since neither maps onto one of them</remarks>
    public override uint GetControllingDrives(int axis)
        => (axis == RadiusDrive || axis == TurntableDrive) ? LowestDrives(2) : base.GetControllingDrives(axis);

    /// <summary>
    /// Whether the head can be put at the given XY position
    /// </summary>
    /// <param name="x">X coordinate in mm</param>
    /// <param name="y">Y coordinate in mm</param>
    /// <returns>True if it is within the annulus the arm sweeps</returns>
    public bool IsReachable(float x, float y)
    {
        float radiusSquared = (x * x) + (y * y);
        return radiusSquared >= MinRadius * MinRadius && radiusSquared <= MaxRadius * MaxRadius;
    }
}
