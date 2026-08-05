using System;
using DuetControlServer.Link.Native;

namespace DuetControlServer.Motion.Kinematics;

/// <summary>
/// The linear delta geometry: three vertical carriages joined to the effector by fixed-length rods
/// </summary>
/// <remarks>
/// <para>
/// Ported from RepRapFirmware's <c>LinearDeltaKinematics</c>. Each carriage runs up a tower at a fixed
/// point on the bed, and the effector hangs from all three on rods of a fixed length. The height a
/// carriage must be at is therefore the height of the effector plus the vertical part of its rod, and
/// the vertical part follows from the rod length and how far the effector is from that tower
/// horizontally. That is the inverse transform, and it is a closed form.
/// </para>
/// <para>
/// The forward transform is the harder direction: given three carriage heights, find the one point
/// that is the right distance from all three. RepRapFirmware solves it by trilateration - the same
/// algebra as GPS - which reduces to a quadratic in Z whose lower root is the effector, the upper one
/// being the mirror-image solution above the towers
/// </para>
/// <para>
/// Beyond the usual three towers the geometry allows up to six, which is how machines with paired
/// carriages are described. The extra towers take part in the inverse transform and in the height
/// limit but not in the forward transform, which needs exactly three distances to trilaterate
/// </para>
/// </remarks>
internal sealed class LinearDeltaKinematicsEngine : KinematicsEngine
{
    /// <summary>Most towers a delta may have</summary>
    public const int MaxTowers = 6;

    /// <summary>Towers the forward transform uses, and the ones that get an angle correction</summary>
    public const int UsualNumTowers = 3;

    private const int TowerA = 0, TowerB = 1, TowerC = 2;
    private const int XAxis = 0, YAxis = 1, ZAxis = 2;

    private const float DegreesToRadians = MathF.PI / 180.0f;

    /// <summary>Rod length for each tower, mm</summary>
    private readonly float[] _diagonals;

    /// <summary>Correction to each tower's nominal angle round the bed, degrees</summary>
    private readonly float[] _angleCorrections;

    /// <summary>How far each endstop is from where it ought to be, mm</summary>
    private readonly float[] _endstopAdjustments;

    /// <summary>Where each tower stands, mm</summary>
    private readonly float[] _towerX = new float[MaxTowers], _towerY = new float[MaxTowers];

    /// <summary>Carriage height for each tower when the machine is homed, mm</summary>
    private readonly float[] _homedCarriageHeights = new float[MaxTowers];

    /// <summary>Squares of the rod lengths, which is the form the transforms want them in</summary>
    private readonly float[] _diagonalsSquared = new float[MaxTowers];

    // Differences between tower positions, precomputed for the forward transform
    private float _xBc, _xCa, _xAb, _yBc, _yCa, _yAb;
    private float _coreKa, _coreKb, _coreKc;
    private float _q, _qSquared;

    /// <summary>Number of towers</summary>
    public int NumTowers { get; }

    /// <summary>Nominal distance from the centre of the bed to each tower, mm</summary>
    public float Radius { get; }

    /// <summary>How far above the bed the effector is when homed, mm</summary>
    public float HomedHeight { get; }

    /// <summary>How far from the centre the effector may go, mm</summary>
    public float PrintRadius { get; }

    /// <summary>How much Z rises per mm of +X movement, to square a tilted bed up</summary>
    public float XTilt { get; }

    /// <summary>How much Z rises per mm of +Y movement</summary>
    public float YTilt { get; }

    /// <summary>
    /// Height that is reachable wherever the effector is in XY, mm
    /// </summary>
    /// <remarks>
    /// A delta's ceiling sags away from the centre, because a carriage cannot go above its endstop and
    /// a rod tilted out to the edge of the bed uses more of its length horizontally. Below this height
    /// no XY position can be out of reach, which lets the position limiter skip the per-tower check
    /// </remarks>
    public float AlwaysReachableHeight { get; private set; }

    /// <inheritdoc />
    public override string Name => "delta";

    /// <inheritdoc />
    /// <remarks>Each motor has its own endstop, so a homing move addresses the motors directly</remarks>
    public override bool HomesIndividualDrives => true;

    /// <inheritdoc />
    /// <remarks>A tower's switch is at the top of its travel, so the answer is the carriage height</remarks>
    public override float GetEndstopPosition(int drive, bool highEnd, float axisMin, float axisMax,
                                             ReadOnlySpan<int> endPoints, ReadOnlySpan<float> stepsPerMm)
        => drive < NumTowers && highEnd
            ? GetHomedCarriageHeight(drive)
            : base.GetEndstopPosition(drive, highEnd, axisMin, axisMax, endPoints, stepsPerMm);


    /// <summary>
    /// Create a delta geometry
    /// </summary>
    /// <param name="numTowers">Number of towers, 3 to 6</param>
    /// <param name="radius">Distance from the centre of the bed to each tower, mm</param>
    /// <param name="diagonals">Rod length for each tower, mm</param>
    /// <param name="angleCorrections">Correction to the nominal angle of the first three towers, degrees</param>
    /// <param name="endstopAdjustments">How far each endstop is from where it ought to be, mm</param>
    /// <param name="homedHeight">Effector height when homed, mm</param>
    /// <param name="printRadius">How far from the centre the effector may go, mm</param>
    /// <param name="xTilt">Z rise per mm of +X movement</param>
    /// <param name="yTilt">Z rise per mm of +Y movement</param>
    public LinearDeltaKinematicsEngine(
        int numTowers,
        float radius,
        ReadOnlySpan<float> diagonals,
        ReadOnlySpan<float> angleCorrections,
        ReadOnlySpan<float> endstopAdjustments,
        float homedHeight,
        float printRadius,
        float xTilt = 0.0f,
        float yTilt = 0.0f)
    {
        NumTowers = Math.Clamp(numTowers, UsualNumTowers, MaxTowers);
        Radius = radius;
        HomedHeight = homedHeight;
        PrintRadius = printRadius;
        XTilt = xTilt;
        YTilt = yTilt;

        _diagonals = new float[MaxTowers];
        _angleCorrections = new float[UsualNumTowers];
        _endstopAdjustments = new float[MaxTowers];

        for (int tower = 0; tower < MaxTowers; tower++)
        {
            _diagonals[tower] = tower < diagonals.Length ? diagonals[tower] : DefaultDiagonal;
            _endstopAdjustments[tower] = tower < endstopAdjustments.Length ? endstopAdjustments[tower] : 0.0f;
        }
        for (int tower = 0; tower < UsualNumTowers; tower++)
        {
            _angleCorrections[tower] = tower < angleCorrections.Length ? angleCorrections[tower] : 0.0f;
        }

        Recalculate();
    }

    /// <summary>Rod length RepRapFirmware assumes until M665 says otherwise, mm</summary>
    public const float DefaultDiagonal = 215.0f;

    /// <summary>Delta radius RepRapFirmware assumes until M665 says otherwise, mm</summary>
    public const float DefaultDeltaRadius = 105.6f;

    /// <summary>Print radius RepRapFirmware assumes until M665 says otherwise, mm</summary>
    public const float DefaultPrintRadius = 80.0f;

    /// <summary>Homed height RepRapFirmware assumes until M665 says otherwise, mm</summary>
    public const float DefaultHomedHeight = 240.0f;

    /// <summary>
    /// A delta with RepRapFirmware's defaults, for before M665 has been seen
    /// </summary>
    /// <returns>The engine</returns>
    public static LinearDeltaKinematicsEngine CreateDefault()
        => new(UsualNumTowers, DefaultDeltaRadius,
               [DefaultDiagonal, DefaultDiagonal, DefaultDiagonal],
               [0.0f, 0.0f, 0.0f], [0.0f, 0.0f, 0.0f],
               DefaultHomedHeight, DefaultPrintRadius);

    /// <summary>Where a tower stands, mm</summary>
    /// <param name="tower">Tower number</param>
    /// <returns>Its X coordinate</returns>
    public float GetTowerX(int tower) => _towerX[tower];

    /// <summary>Where a tower stands, mm</summary>
    /// <param name="tower">Tower number</param>
    /// <returns>Its Y coordinate</returns>
    public float GetTowerY(int tower) => _towerY[tower];

    /// <summary>Carriage height for a tower when the machine is homed, mm</summary>
    /// <param name="tower">Tower number</param>
    /// <returns>The height</returns>
    public float GetHomedCarriageHeight(int tower) => _homedCarriageHeights[tower];

    /// <inheritdoc />
    public override NativeMovementError CartesianToMotorSteps(
        ReadOnlySpan<float> machinePos,
        ReadOnlySpan<float> stepsPerMm,
        int numVisibleAxes,
        int numTotalAxes,
        Span<int> motorPos,
        bool isCoordinated = false)
    {
        if (machinePos.Length < UsualNumTowers)
        {
            return NativeMovementError.UnreachablePosition;
        }

        NativeMovementError result = NativeMovementError.Ok;
        int towerLimit = Math.Min(NumTowers, Math.Min(motorPos.Length, stepsPerMm.Length));

        for (int tower = 0; tower < towerLimit; tower++)
        {
            float carriageHeight = Transform(machinePos, tower);
            if (float.IsNaN(carriageHeight) || float.IsInfinity(carriageHeight))
            {
                // The rod is not long enough to span the gap, so there is no carriage height that
                // puts the effector there at all
                result = NativeMovementError.UnreachablePosition;
            }
            else if (TryRoundToInt32(carriageHeight * stepsPerMm[tower], out int steps))
            {
                motorPos[tower] = steps;
            }
            else
            {
                result = NativeMovementError.MicrostepPositionTooLarge;
            }
        }

        NativeMovementError linearResult = LinearAxesToMotorSteps(machinePos, stepsPerMm, NumTowers, numVisibleAxes, motorPos);
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
        if (machinePos.Length < UsualNumTowers || motorPos.Length < UsualNumTowers || stepsPerMm.Length < UsualNumTowers)
        {
            return;
        }

        ForwardTransform(
            motorPos[TowerA] / stepsPerMm[TowerA],
            motorPos[TowerB] / stepsPerMm[TowerB],
            motorPos[TowerC] / stepsPerMm[TowerC],
            machinePos);

        LinearMotorStepsToCartesian(motorPos, stepsPerMm, NumTowers, numVisibleAxes, machinePos);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every tower moves for any XYZ movement, so all three motors have to be energised to hold the
    /// effector still even when only one axis is being commanded
    /// </remarks>
    public override uint GetControllingDrives(int axis)
        => (axis >= 0 && axis <= ZAxis) ? LowestDrives(NumTowers) : base.GetControllingDrives(axis);

    /// <inheritdoc />
    public override float GetTiltCorrection(int axis)
        => axis == XAxis ? XTilt : axis == YAxis ? YTilt : 0.0f;

    /// <summary>
    /// Whether a position is inside the printable cylinder and under the reachable ceiling
    /// </summary>
    /// <param name="machinePos">Axis coordinates in mm</param>
    /// <returns>True if the effector can be put there</returns>
    /// <remarks>
    /// The two limits are of different kinds. The print radius is configuration - it is where the user
    /// said the bed ends - while the height limit is the machine itself: above it a carriage would
    /// have to go past its endstop
    /// </remarks>
    public bool IsReachable(ReadOnlySpan<float> machinePos)
    {
        if (machinePos.Length < UsualNumTowers)
        {
            return false;
        }

        float radiusSquared = (machinePos[XAxis] * machinePos[XAxis]) + (machinePos[YAxis] * machinePos[YAxis]);
        if (radiusSquared > PrintRadius * PrintRadius)
        {
            return false;
        }

        if (machinePos[ZAxis] <= AlwaysReachableHeight)
        {
            return true;
        }

        for (int tower = 0; tower < UsualNumTowers; tower++)
        {
            float carriageHeight = Transform(machinePos, tower);
            if (float.IsNaN(carriageHeight) || carriageHeight > _homedCarriageHeights[tower])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The carriage height one tower needs for the effector to be at the given position
    /// </summary>
    /// <param name="machinePos">Axis coordinates in mm</param>
    /// <param name="tower">Tower number</param>
    /// <returns>The height in mm, or NaN if the rod cannot span the gap</returns>
    private float Transform(ReadOnlySpan<float> machinePos, int tower)
    {
        if (tower >= NumTowers)
        {
            return machinePos[tower];
        }

        float dx = machinePos[XAxis] - _towerX[tower];
        float dy = machinePos[YAxis] - _towerY[tower];

        // Pythagoras on the rod: what is left of its length after the horizontal part is the vertical
        // drop from the carriage to the effector
        return MathF.Sqrt(_diagonalsSquared[tower] - (dx * dx) - (dy * dy))
               + machinePos[ZAxis]
               + (machinePos[XAxis] * XTilt)
               + (machinePos[YAxis] * YTilt);
    }

    /// <summary>
    /// Find the effector position from three carriage heights
    /// </summary>
    /// <param name="ha">Height of the A carriage, mm</param>
    /// <param name="hb">Height of the B carriage, mm</param>
    /// <param name="hc">Height of the C carriage, mm</param>
    /// <param name="machinePos">Filled in with X, Y and Z in mm</param>
    /// <remarks>
    /// Trilateration. Subtracting each pair of sphere equations kills the quadratic terms and leaves
    /// two planes, whose intersection is a line; parametrising X and Y along that line by Z and
    /// substituting back into one sphere gives a quadratic in Z. The lower root is the effector - the
    /// upper one is the reflection of it above the carriages, which the machine cannot reach
    /// </remarks>
    private void ForwardTransform(float ha, float hb, float hc, Span<float> machinePos)
    {
        // x = (Uz + S)/Q and y = -(Rz + T)/Q describe the line the effector must be on
        float r = ((_xBc * ha) + (_xCa * hb) + (_xAb * hc)) * 2.0f;
        float u = ((_yBc * ha) + (_yCa * hb) + (_yAb * hc)) * 2.0f;

        // Ka + Kb + Kc is identically zero, so one of these carries no information the others lack
        float ka = _coreKa + ((hc * hc) - (hb * hb));
        float kb = _coreKb + ((ha * ha) - (hc * hc));
        float kc = _coreKc + ((hb * hb) - (ha * ha));

        float s = (ka * _towerY[TowerA]) + (kb * _towerY[TowerB]) + (kc * _towerY[TowerC]);
        float t = (ka * _towerX[TowerA]) + (kb * _towerX[TowerB]) + (kc * _towerX[TowerC]);

        float a = (u * u) + (r * r) + _qSquared;
        float minusHalfB = (_qSquared * ha)
                           + (_q * ((u * _towerX[TowerA]) - (r * _towerY[TowerA])))
                           - ((r * t) + (u * s));
        float c = Square((_towerX[TowerA] * _q) - s)
                  + Square((_towerY[TowerA] * _q) + t)
                  + (((ha * ha) - _diagonalsSquared[TowerA]) * _qSquared);

        float z = (minusHalfB - MathF.Sqrt((minusHalfB * minusHalfB) - (a * c))) / a;
        machinePos[XAxis] = ((u * z) + s) / _q;
        machinePos[YAxis] = -((r * z) + t) / _q;

        // The tilt correction went into the carriage heights on the way out, so it comes back off here
        machinePos[ZAxis] = z - ((machinePos[XAxis] * XTilt) + (machinePos[YAxis] * YTilt));
    }

    /// <summary>
    /// Work out the tower positions and the forward transform's constants
    /// </summary>
    private void Recalculate()
    {
        // A is at -150 degrees, B at -30 and C at +90, i.e. evenly spaced with C to the rear
        _towerX[TowerA] = -(Radius * MathF.Cos((30.0f + _angleCorrections[TowerA]) * DegreesToRadians));
        _towerY[TowerA] = -(Radius * MathF.Sin((30.0f + _angleCorrections[TowerA]) * DegreesToRadians));
        _towerX[TowerB] = +(Radius * MathF.Cos((30.0f - _angleCorrections[TowerB]) * DegreesToRadians));
        _towerY[TowerB] = -(Radius * MathF.Sin((30.0f - _angleCorrections[TowerB]) * DegreesToRadians));
        _towerX[TowerC] = -(Radius * MathF.Sin(_angleCorrections[TowerC] * DegreesToRadians));
        _towerY[TowerC] = +(Radius * MathF.Cos(_angleCorrections[TowerC] * DegreesToRadians));

        _xBc = _towerX[TowerC] - _towerX[TowerB];
        _xCa = _towerX[TowerA] - _towerX[TowerC];
        _xAb = _towerX[TowerB] - _towerX[TowerA];
        _yBc = _towerY[TowerC] - _towerY[TowerB];
        _yCa = _towerY[TowerA] - _towerY[TowerC];
        _yAb = _towerY[TowerB] - _towerY[TowerA];

        // Twice the signed area of the tower triangle. Zero would mean the three towers are in a line,
        // which does not pin the effector down
        _q = ((_xAb * _towerY[TowerC]) + (_xCa * _towerY[TowerB]) + (_xBc * _towerY[TowerA])) * 2.0f;
        _qSquared = _q * _q;

        AlwaysReachableHeight = HomedHeight;
        for (int tower = 0; tower < NumTowers; tower++)
        {
            _diagonalsSquared[tower] = _diagonals[tower] * _diagonals[tower];

            // Homing puts the effector at the centre of the bed at the homed height, so the carriage
            // height that corresponds to is the homed height plus the rod's vertical part there
            float horizontalOffsetSquared = tower < UsualNumTowers
                ? Radius * Radius
                : (_towerX[tower] * _towerX[tower]) + (_towerY[tower] * _towerY[tower]);
            _homedCarriageHeights[tower] = HomedHeight
                                           + MathF.Sqrt(_diagonalsSquared[tower] - horizontalOffsetSquared)
                                           + _endstopAdjustments[tower];

            // With the rod straight out sideways the effector is level with the carriage; a rod that
            // is vertical puts it a full rod length below. The latter is the worst case
            float heightLimit = _homedCarriageHeights[tower] - _diagonals[tower];
            if (heightLimit < AlwaysReachableHeight)
            {
                AlwaysReachableHeight = heightLimit;
            }
        }

        float coreFa = (_towerX[TowerA] * _towerX[TowerA]) + (_towerY[TowerA] * _towerY[TowerA]);
        float coreFb = (_towerX[TowerB] * _towerX[TowerB]) + (_towerY[TowerB] * _towerY[TowerB]);
        float coreFc = (_towerX[TowerC] * _towerX[TowerC]) + (_towerY[TowerC] * _towerY[TowerC]);
        _coreKa = (_diagonalsSquared[TowerB] - _diagonalsSquared[TowerC]) + (coreFc - coreFb);
        _coreKb = (_diagonalsSquared[TowerC] - _diagonalsSquared[TowerA]) + (coreFa - coreFc);
        _coreKc = (_diagonalsSquared[TowerA] - _diagonalsSquared[TowerB]) + (coreFb - coreFa);
    }

    private static float Square(float value) => value * value;

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
