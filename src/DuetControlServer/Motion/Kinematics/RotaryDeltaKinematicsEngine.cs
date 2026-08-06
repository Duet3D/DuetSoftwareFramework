using System;
using DuetControlServer.Link.Native;

namespace DuetControlServer.Motion.Kinematics;

/// <summary>
/// The rotary delta geometry: three arms swung by motors at fixed bearings, with rods to the effector
/// </summary>
/// <remarks>
/// <para>
/// Ported from RepRapFirmware's <c>RotaryDeltaKinematics</c>. It is a delta like the linear one, but
/// the thing the motor controls is an angle rather than a height: each motor swings a rigid arm about
/// a bearing, and a rod of fixed length runs from the far end of that arm to the effector. Steps per
/// mm on the first three drives are therefore steps per degree.
/// </para>
/// <para>
/// The inverse transform solves <c>a cos(theta) + b sin(theta) = c</c> for each arm, which is the
/// condition that the rod reaches the effector. Squaring it gives a quadratic in sin(theta) with two
/// roots - arm swung up or swung down to reach the same point - of which the lower is taken.
/// </para>
/// <para>
/// The forward transform trilaterates from the three rod-end positions, exactly as the linear delta
/// does, only here those ends move on circles rather than straight up and down
/// </para>
/// </remarks>
internal sealed class RotaryDeltaKinematicsEngine : KinematicsEngine
{
    /// <summary>Number of arms, which is not configurable</summary>
    public const int DeltaAxes = 3;

    private const int TowerA = 0, TowerB = 1, TowerC = 2;
    private const int XAxis = 0, YAxis = 1;

    private const float DegreesToRadians = MathF.PI / 180.0f;
    private const float RadiansToDegrees = 180.0f / MathF.PI;

    /// <summary>Where each arm's bearing sits round the bed, degrees</summary>
    private static readonly float[] NormalTowerAngles = [-150.0f, -30.0f, 90.0f];

    private readonly float[] _bearingHeights = new float[DeltaAxes];
    private readonly float[] _armLengths = new float[DeltaAxes];
    private readonly float[] _rodLengths = new float[DeltaAxes];
    private readonly float[] _angleCorrections = new float[DeltaAxes];
    private readonly float[] _endstopAdjustments = new float[DeltaAxes];

    // Derived values, all indexed by tower
    private readonly float[] _armAngleCosines = new float[DeltaAxes];
    private readonly float[] _armAngleSines = new float[DeltaAxes];
    private readonly float[] _twiceU = new float[DeltaAxes];
    private readonly float[] _rodSquared = new float[DeltaAxes];
    private readonly float[] _rodSquaredMinusArmSquared = new float[DeltaAxes];

    /// <summary>Distance from the centre of the bed to each bearing, mm</summary>
    public float Radius { get; }

    /// <summary>How far from the centre the effector may go, mm</summary>
    public float PrintRadius { get; }

    /// <summary>Lowest arm angle the machine allows, degrees</summary>
    public float MinArmAngle { get; }

    /// <summary>Highest arm angle the machine allows, degrees</summary>
    public float MaxArmAngle { get; }

    /// <inheritdoc />
    public override string Name => "Rotary delta";

    /// <inheritdoc />
    /// <remarks>Each motor has its own endstop, so a homing move addresses the motors directly</remarks>
    public override bool HomesIndividualDrives => true;
    /// <inheritdoc />
    /// <remarks>Every axis of the head is an arm angle, so Z bows like the rest</remarks>
    public override SegmentationType Segmentation => SegmentationType.Segment | SegmentationType.IncludeZ | SegmentationType.IncludeG0;


    /// <inheritdoc />
    /// <remarks>
    /// The effector's position is a function of all three towers, so none of them means anything
    /// until every one is homed - whatever M564 says about moving before homing
    /// </remarks>
    public override uint MustBeHomedAxes(uint axesMoving, bool disallowMovesBeforeHoming)
    {
        const uint xyzAxes = (1u << XAxis) | (1u << YAxis) | (1u << ZAxis);
        return (axesMoving & xyzAxes) != 0 ? axesMoving | xyzAxes : axesMoving;
    }


    /// <inheritdoc />
    /// <remarks>An arm's switch is at the top of its swing, adjusted per tower by M666</remarks>
    public override float GetEndstopPosition(int drive, bool highEnd, float axisMin, float axisMax,
                                             ReadOnlySpan<int> endPoints, ReadOnlySpan<float> stepsPerMm)
        => drive < DeltaAxes && highEnd
            ? MaxArmAngle + GetEndstopAdjustment(drive)
            : base.GetEndstopPosition(drive, highEnd, axisMin, axisMax, endPoints, stepsPerMm);


    /// <summary>Arm length RepRapFirmware assumes until M669 says otherwise, mm</summary>
    public const float DefaultArmLength = 100.0f;

    /// <summary>Rod length RepRapFirmware assumes until M669 says otherwise, mm</summary>
    public const float DefaultRodLength = 200.0f;

    /// <summary>Delta radius RepRapFirmware assumes until M669 says otherwise, mm</summary>
    public const float DefaultDeltaRadius = 50.0f;

    /// <summary>Print radius RepRapFirmware assumes until M669 says otherwise, mm</summary>
    public const float DefaultPrintRadius = 80.0f;

    /// <summary>Bearing height RepRapFirmware assumes until M669 says otherwise, mm</summary>
    public const float DefaultBearingHeight = 250.0f;

    /// <summary>Lowest arm angle RepRapFirmware assumes until M669 says otherwise, degrees</summary>
    public const float DefaultMinArmAngle = -45.0f;

    /// <summary>Highest arm angle RepRapFirmware assumes until M669 says otherwise, degrees</summary>
    public const float DefaultMaxArmAngle = 45.0f;

    /// <summary>
    /// Create a rotary delta geometry
    /// </summary>
    /// <param name="radius">Distance from the centre of the bed to each bearing, mm</param>
    /// <param name="armLengths">Length of each arm, mm</param>
    /// <param name="rodLengths">Length of each rod, mm</param>
    /// <param name="bearingHeights">Height of each bearing above the bed, mm</param>
    /// <param name="angleCorrections">Correction to each arm's nominal angle round the bed, degrees</param>
    /// <param name="endstopAdjustments">How far each endstop is from where it ought to be, degrees</param>
    /// <param name="printRadius">How far from the centre the effector may go, mm</param>
    /// <param name="minArmAngle">Lowest arm angle the machine allows, degrees</param>
    /// <param name="maxArmAngle">Highest arm angle the machine allows, degrees</param>
    public RotaryDeltaKinematicsEngine(
        float radius = DefaultDeltaRadius,
        ReadOnlySpan<float> armLengths = default,
        ReadOnlySpan<float> rodLengths = default,
        ReadOnlySpan<float> bearingHeights = default,
        ReadOnlySpan<float> angleCorrections = default,
        ReadOnlySpan<float> endstopAdjustments = default,
        float printRadius = DefaultPrintRadius,
        float minArmAngle = DefaultMinArmAngle,
        float maxArmAngle = DefaultMaxArmAngle)
    {
        Radius = radius;
        PrintRadius = printRadius;
        MinArmAngle = minArmAngle;
        MaxArmAngle = maxArmAngle;

        for (int tower = 0; tower < DeltaAxes; tower++)
        {
            _armLengths[tower] = tower < armLengths.Length ? armLengths[tower] : DefaultArmLength;
            _rodLengths[tower] = tower < rodLengths.Length ? rodLengths[tower] : DefaultRodLength;
            _bearingHeights[tower] = tower < bearingHeights.Length ? bearingHeights[tower] : DefaultBearingHeight;
            _angleCorrections[tower] = tower < angleCorrections.Length ? angleCorrections[tower] : 0.0f;
            _endstopAdjustments[tower] = tower < endstopAdjustments.Length ? endstopAdjustments[tower] : 0.0f;
        }

        Recalculate();
    }

    /// <summary>How far each endstop is from where it ought to be, degrees</summary>
    /// <param name="tower">Tower number</param>
    /// <returns>The adjustment</returns>
    public float GetEndstopAdjustment(int tower) => _endstopAdjustments[tower];

    /// <inheritdoc />
    public override NativeMovementError CartesianToMotorSteps(
        ReadOnlySpan<float> machinePos,
        ReadOnlySpan<float> stepsPerMm,
        int numVisibleAxes,
        int numTotalAxes,
        Span<int> motorPos,
        bool isCoordinated = false)
    {
        if (machinePos.Length < DeltaAxes)
        {
            return NativeMovementError.UnreachablePosition;
        }

        NativeMovementError result = NativeMovementError.Ok;
        int towerLimit = Math.Min(Math.Min(numVisibleAxes, DeltaAxes), Math.Min(motorPos.Length, stepsPerMm.Length));

        for (int tower = 0; tower < towerLimit; tower++)
        {
            float angle = Transform(machinePos, tower);
            if (float.IsNaN(angle) || float.IsInfinity(angle))
            {
                // The rod cannot reach the effector from anywhere on the arm's circle
                result = NativeMovementError.UnreachablePosition;
            }
            else if (TryRoundToInt32(angle * stepsPerMm[tower], out int steps))
            {
                motorPos[tower] = steps;
            }
            else
            {
                result = NativeMovementError.MicrostepPositionTooLarge;
            }
        }

        NativeMovementError linearResult = LinearAxesToMotorSteps(machinePos, stepsPerMm, DeltaAxes, numVisibleAxes, motorPos);
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
        if (machinePos.Length < DeltaAxes || motorPos.Length < DeltaAxes || stepsPerMm.Length < DeltaAxes)
        {
            return;
        }

        ForwardTransform(
            motorPos[TowerA] / stepsPerMm[TowerA],
            motorPos[TowerB] / stepsPerMm[TowerB],
            motorPos[TowerC] / stepsPerMm[TowerC],
            machinePos);

        LinearMotorStepsToCartesian(motorPos, stepsPerMm, DeltaAxes, numVisibleAxes, machinePos);
    }

    /// <inheritdoc />
    /// <remarks>All three arms swing for any XYZ movement, so all three motors have to be energised</remarks>
    public override uint GetControllingDrives(int axis)
        => (axis >= 0 && axis <= ZAxis) ? LowestDrives(DeltaAxes) : base.GetControllingDrives(axis);

    /// <summary>
    /// Whether a position is inside the printable cylinder and within the arms' angular range
    /// </summary>
    /// <param name="machinePos">Axis coordinates in mm</param>
    /// <returns>True if the effector can be put there</returns>
    public bool IsReachable(ReadOnlySpan<float> machinePos)
    {
        if (machinePos.Length < DeltaAxes)
        {
            return false;
        }

        float radiusSquared = (machinePos[XAxis] * machinePos[XAxis]) + (machinePos[YAxis] * machinePos[YAxis]);
        if (radiusSquared > PrintRadius * PrintRadius)
        {
            return false;
        }

        for (int tower = 0; tower < DeltaAxes; tower++)
        {
            float angle = Transform(machinePos, tower);
            if (float.IsNaN(angle) || angle < MinArmAngle || angle > MaxArmAngle)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The angle one arm must be swung to for the effector to be at the given position
    /// </summary>
    /// <param name="machinePos">Axis coordinates in mm</param>
    /// <param name="tower">Tower number</param>
    /// <returns>The angle in degrees, or NaN if the rod cannot reach</returns>
    /// <remarks>
    /// <para>
    /// With the coordinates rotated so +X runs along the arm, the rod length gives
    /// <c>L^2 = (U cos(theta) + (R - x))^2 + y^2 + (U sin(theta) + (H - z))^2</c>. Expanding and using
    /// <c>sin^2 + cos^2 = 1</c> reduces it to <c>a cos(theta) + b sin(theta) = c</c>; squaring that
    /// gives a quadratic in sin(theta), of whose two roots the lower arm position is wanted.
    /// </para>
    /// <para>
    /// Squaring is what makes the second root available, and it also makes it possible for the root
    /// taken to solve the squared equation but not the original one - which happens above the
    /// bearings, where the arm would have to be swung up rather than down. A rotary delta works below
    /// its bearings, so RepRapFirmware takes the lower root unconditionally and so does this
    /// </para>
    /// </remarks>
    private float Transform(ReadOnlySpan<float> machinePos, int tower)
    {
        if (tower >= DeltaAxes)
        {
            return machinePos[tower];
        }

        // Rotate so that +X points along this arm and +Y is 90 degrees anticlockwise from it
        float x = (machinePos[XAxis] * _armAngleCosines[tower]) + (machinePos[YAxis] * _armAngleSines[tower]);
        float y = (machinePos[YAxis] * _armAngleCosines[tower]) - (machinePos[XAxis] * _armAngleSines[tower]);

        float rMinusX = Radius - x;
        float hMinusZ = _bearingHeights[tower] - machinePos[ZAxis];
        float a = _twiceU[tower] * rMinusX;
        float b = _twiceU[tower] * hMinusZ;
        float c = _rodSquaredMinusArmSquared[tower] - ((hMinusZ * hMinusZ) + (rMinusX * rMinusX) + (y * y));

        float sinTheta = ((b * c) - (a * MathF.Sqrt((a * a) + (b * b) - (c * c)))) / ((a * a) + (b * b));
        return MathF.Asin(sinTheta) * RadiansToDegrees;
    }

    /// <summary>
    /// Find the effector position from three arm angles
    /// </summary>
    /// <param name="ha">Angle of the A arm, degrees</param>
    /// <param name="hb">Angle of the B arm, degrees</param>
    /// <param name="hc">Angle of the C arm, degrees</param>
    /// <param name="machinePos">Filled in with X, Y and Z in mm</param>
    /// <remarks>
    /// The arm angles put the three rod ends at known points; the effector is then the one point at
    /// the right rod length from each of them, which is trilateration as for the linear delta
    /// </remarks>
    private void ForwardTransform(float ha, float hb, float hc, Span<float> machinePos)
    {
        // Where each rod's upper end has been swung to. Note that RepRapFirmware indexes the arm
        // length with the A tower in all three Z terms here; that is a typo, and using each tower's
        // own arm length is what the geometry says. It makes no difference on a machine whose arms
        // are all the same length, which is every machine the typo has been seen on
        float angleA = ha * DegreesToRadians;
        float posAX = (Radius + (_armLengths[TowerA] * MathF.Cos(angleA))) * _armAngleCosines[TowerA];
        float posAY = (Radius + (_armLengths[TowerA] * MathF.Cos(angleA))) * _armAngleSines[TowerA];
        float posAZ = _bearingHeights[TowerA] + (_armLengths[TowerA] * MathF.Sin(angleA));

        float angleB = hb * DegreesToRadians;
        float posBX = (Radius + (_armLengths[TowerB] * MathF.Cos(angleB))) * _armAngleCosines[TowerB];
        float posBY = (Radius + (_armLengths[TowerB] * MathF.Cos(angleB))) * _armAngleSines[TowerB];
        float posBZ = _bearingHeights[TowerB] + (_armLengths[TowerB] * MathF.Sin(angleB));

        float angleC = hc * DegreesToRadians;
        float posCX = (Radius + (_armLengths[TowerC] * MathF.Cos(angleC))) * _armAngleCosines[TowerC];
        float posCY = (Radius + (_armLengths[TowerC] * MathF.Cos(angleC))) * _armAngleSines[TowerC];
        float posCZ = _bearingHeights[TowerC] + (_armLengths[TowerC] * MathF.Sin(angleC));

        float da2 = (posAX * posAX) + (posAY * posAY) + (posAZ * posAZ);
        float db2 = (posBX * posBX) + (posBY * posBY) + (posBZ * posBZ);
        float dc2 = (posCX * posCX) + (posCY * posCY) + (posCZ * posCZ);

        // x = (Qz + S)/P and y = -(Rz + T)/P describe the line the effector must be on
        float p = ((posBX * posCY) - (posAX * posCY) - (posCX * posBY) + (posAX * posBY) + (posCX * posAY) - (posBX * posAY)) * 2.0f;
        float q = (((posBY - posAY) * posCZ) + ((posAY - posCY) * posBZ) + ((posCY - posBY) * posAZ)) * 2.0f;
        float r = (((posBX - posAX) * posCZ) + ((posAX - posCX) * posBZ) + ((posCX - posBX) * posAZ)) * 2.0f;

        float s = ((_rodSquared[TowerA] - _rodSquared[TowerB] + db2 - da2) * posCY)
                  + ((_rodSquared[TowerC] - _rodSquared[TowerA] + da2 - dc2) * posBY)
                  + ((_rodSquared[TowerB] - _rodSquared[TowerC] + dc2 - db2) * posAY);
        float t = ((_rodSquared[TowerA] - _rodSquared[TowerB] + db2 - da2) * posCX)
                  + ((_rodSquared[TowerC] - _rodSquared[TowerA] + da2 - dc2) * posBX)
                  + ((_rodSquared[TowerB] - _rodSquared[TowerC] + dc2 - db2) * posAX);

        float p2 = p * p;
        float a = p2 + (q * q) + (r * r);
        float halfB = (p * r * posAY) - (p2 * posAZ) - (p * q * posAX) + (r * t) + (q * s);
        float c = (s * s) + (t * t) + (((t * posAY) - (s * posAX)) * p * 2.0f) + ((da2 - _rodSquared[TowerA]) * p2);

        float z = (-halfB - MathF.Sqrt((halfB * halfB) - (a * c))) / a;

        machinePos[XAxis] = ((q * z) + s) / p;
        machinePos[YAxis] = -((r * z) + t) / p;
        machinePos[ZAxis] = z;
    }

    /// <summary>
    /// Work out the arm directions and the constants both transforms need
    /// </summary>
    private void Recalculate()
    {
        for (int tower = 0; tower < DeltaAxes; tower++)
        {
            float angle = (NormalTowerAngles[tower] + _angleCorrections[tower]) * DegreesToRadians;
            _armAngleSines[tower] = MathF.Sin(angle);
            _armAngleCosines[tower] = MathF.Cos(angle);
            _twiceU[tower] = _armLengths[tower] * 2.0f;
            _rodSquared[tower] = _rodLengths[tower] * _rodLengths[tower];
            _rodSquaredMinusArmSquared[tower] = _rodSquared[tower] - (_armLengths[tower] * _armLengths[tower]);
        }
    }

    /// <summary>
    /// Which macro to run next to home some of a set of axes
    /// </summary>
    /// <param name="toBeHomed">Axes still to home, as a bitmap</param>
    /// <param name="alreadyHomed">Axes already homed, as a bitmap</param>
    /// <param name="axisLetters">Letter of each axis, in axis order</param>
    /// <param name="fileName">The macro to run</param>
    /// <returns>Axes that have to be homed first, always none here</returns>
    /// <remarks>
    /// Homing one tower of a delta is meaningless: no carriage moves without the other two, and the
    /// effector is only where the three of them put it. So any of X, Y or Z homes all of them
    /// </remarks>
    public override uint GetHomingFileName(uint toBeHomed, uint alreadyHomed, ReadOnlySpan<char> axisLetters,
                                           out string fileName)
    {
        const uint xyz = 0b111;
        if ((toBeHomed & xyz) != 0)
        {
            fileName = "homedelta.g";
            return 0;
        }
        return base.GetHomingFileName(toBeHomed, alreadyHomed, axisLetters, out fileName);
    }

    /// <inheritdoc />
    /// <remarks>All three towers, because a delta has no axis that moves a motor of its own</remarks>
    public override uint AxesToHomeBeforeProbing => 0b111;
}
