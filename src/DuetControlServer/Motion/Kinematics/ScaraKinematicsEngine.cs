using System;
using DuetControlServer.Link.Native;

namespace DuetControlServer.Motion.Kinematics;

/// <summary>
/// The SCARA geometry: a proximal arm on a fixed pillar, carrying a distal arm with the head on its end
/// </summary>
/// <remarks>
/// <para>
/// Ported from RepRapFirmware's <c>ScaraKinematics</c>. The X motor turns the proximal joint through
/// theta and the Y motor turns the distal joint through psi, so steps per mm on those two drives are
/// really steps per degree. Theta is measured from the +X direction; psi is the distal arm's angle
/// relative to the proximal one.
/// </para>
/// <para>
/// Any point the head can reach, it can reach two ways - elbow bent one way or the other. That choice
/// is the arm mode, and it is state rather than geometry: switching it is itself a movement, so a
/// coordinated move that would need a switch is refused instead. The engine remembers the mode from
/// one call to the next and only reconsiders it when the requested point cannot be reached in the
/// current one.
/// </para>
/// <para>
/// Crosstalk covers machines where turning one joint drags another: proximal onto distal, and either
/// arm onto Z. It is a fixed linear coupling, so it goes in as a correction on the way out and comes
/// back off on the way in
/// </para>
/// </remarks>
internal sealed class ScaraKinematicsEngine : KinematicsEngine
{
    private const int XAxis = 0, YAxis = 1, ZAxis = 2;
    private const int XyzAxes = 3;

    private const float DegreesToRadians = MathF.PI / 180.0f;
    private const float RadiansToDegrees = 180.0f / MathF.PI;

    /// <summary>Proximal to distal, proximal to Z and distal to Z coupling</summary>
    private readonly float[] _crosstalk = new float[3];

    /// <summary>How far the proximal joint may turn, degrees</summary>
    private readonly float[] _thetaLimits = new float[2];

    /// <summary>How far the distal joint may turn, degrees</summary>
    private readonly float[] _psiLimits = new float[2];

    /// <summary>Whether each joint may turn indefinitely rather than being limited</summary>
    private readonly bool[] _supportsContinuousRotation = new bool[2];

    // Derived
    private readonly float _proximalArmLengthSquared, _distalArmLengthSquared, _twoPd;

    // The arm mode, and the position it was last worked out for. Mutable because the mode persists
    // between moves; the builder that calls this is single-threaded, as RepRapFirmware's is
    private float _cachedX = float.NaN, _cachedY = float.NaN, _cachedTheta, _cachedPsi;
    private bool _currentArmMode, _cachedArmMode;

    /// <summary>Length of the proximal arm, mm</summary>
    public float ProximalArmLength { get; }

    /// <summary>Length of the distal arm, mm</summary>
    public float DistalArmLength { get; }

    /// <summary>Where bed X zero is relative to the proximal joint, mm</summary>
    public float XOffset { get; }

    /// <summary>Where bed Y zero is relative to the proximal joint, mm</summary>
    public float YOffset { get; }

    /// <summary>Closest the head may come to the pillar, mm</summary>
    public float MinRadius { get; }

    /// <summary>Furthest the head may go from the pillar, mm</summary>
    public float MaxRadius { get; }

    /// <inheritdoc />
    public override string Name => "Scara";

    /// <inheritdoc />
    public override uint ContinuousRotationAxes
        => (_supportsContinuousRotation[0] ? 1u << XAxis : 0u) | (_supportsContinuousRotation[1] ? 1u << YAxis : 0u);

    /// <summary>Proximal arm length RepRapFirmware assumes until M669 says otherwise, mm</summary>
    public const float DefaultProximalArmLength = 100.0f;

    /// <summary>Distal arm length RepRapFirmware assumes until M669 says otherwise, mm</summary>
    public const float DefaultDistalArmLength = 100.0f;

    /// <summary>Proximal joint range RepRapFirmware assumes until M669 says otherwise, degrees</summary>
    public const float DefaultMinTheta = -90.0f, DefaultMaxTheta = 90.0f;

    /// <summary>Distal joint range RepRapFirmware assumes until M669 says otherwise, degrees</summary>
    public const float DefaultMinPsi = -135.0f, DefaultMaxPsi = 135.0f;

    /// <summary>
    /// Create a SCARA geometry
    /// </summary>
    /// <param name="proximalArmLength">Length of the proximal arm, mm</param>
    /// <param name="distalArmLength">Length of the distal arm, mm</param>
    /// <param name="thetaLimits">How far the proximal joint may turn, degrees</param>
    /// <param name="psiLimits">How far the distal joint may turn, degrees</param>
    /// <param name="crosstalk">Proximal to distal, proximal to Z and distal to Z coupling</param>
    /// <param name="xOffset">Where bed X zero is relative to the proximal joint, mm</param>
    /// <param name="yOffset">Where bed Y zero is relative to the proximal joint, mm</param>
    /// <param name="requestedMinRadius">A minimum radius the user asked for, if it is tighter than the mechanics allow</param>
    public ScaraKinematicsEngine(
        float proximalArmLength = DefaultProximalArmLength,
        float distalArmLength = DefaultDistalArmLength,
        ReadOnlySpan<float> thetaLimits = default,
        ReadOnlySpan<float> psiLimits = default,
        ReadOnlySpan<float> crosstalk = default,
        float xOffset = 0.0f,
        float yOffset = 0.0f,
        float requestedMinRadius = 0.0f)
    {
        ProximalArmLength = proximalArmLength;
        DistalArmLength = distalArmLength;
        XOffset = xOffset;
        YOffset = yOffset;

        _thetaLimits[0] = thetaLimits.Length > 0 ? thetaLimits[0] : DefaultMinTheta;
        _thetaLimits[1] = thetaLimits.Length > 1 ? thetaLimits[1] : DefaultMaxTheta;
        _psiLimits[0] = psiLimits.Length > 0 ? psiLimits[0] : DefaultMinPsi;
        _psiLimits[1] = psiLimits.Length > 1 ? psiLimits[1] : DefaultMaxPsi;
        for (int i = 0; i < 3; i++)
        {
            _crosstalk[i] = i < crosstalk.Length ? crosstalk[i] : 0.0f;
        }

        _proximalArmLengthSquared = proximalArmLength * proximalArmLength;
        _distalArmLengthSquared = distalArmLength * distalArmLength;
        _twoPd = proximalArmLength * distalArmLength * 2.0f;

        // A joint that can turn more than a full circle is not really limited at all
        _supportsContinuousRotation[0] = _thetaLimits[1] - _thetaLimits[0] > 360.0f;
        _supportsContinuousRotation[1] = _psiLimits[1] - _psiLimits[0] > 360.0f;

        // Folded as tightly as the distal joint allows, the head is at its closest to the pillar. The
        // 1.005 keeps a little clear of the singularity where the arms are exactly in line
        float foldedRadius = MathF.Sqrt(_proximalArmLengthSquared + _distalArmLengthSquared
                                        + (_twoPd * MathF.Min(MathF.Cos(_psiLimits[0] * DegreesToRadians),
                                                              MathF.Cos(_psiLimits[1] * DegreesToRadians))));
        MinRadius = MathF.Max(foldedRadius * 1.005f, requestedMinRadius);

        float maxRadius;
        if (_supportsContinuousRotation[1] || (_psiLimits[0] <= 0.0f && _psiLimits[1] >= 0.0f))
        {
            // The arms can be straightened out fully
            maxRadius = proximalArmLength + distalArmLength;
        }
        else
        {
            float minAngle = MathF.Min(MathF.Abs(_psiLimits[0]), MathF.Abs(_psiLimits[1])) * DegreesToRadians;
            maxRadius = MathF.Sqrt(_proximalArmLengthSquared + _distalArmLengthSquared + (_twoPd * MathF.Cos(minAngle)));
        }
        MaxRadius = maxRadius * 0.995f;
    }

    /// <summary>
    /// Which way the elbow is currently bent
    /// </summary>
    /// <remarks>
    /// False is anticlockwise relative to the proximal arm, true is clockwise. Exposed so that a test
    /// or a diagnostic can see the choice the engine made, since it is not visible in the motor positions
    /// </remarks>
    public bool CurrentArmMode => _currentArmMode;

    /// <inheritdoc />
    public override NativeMovementError CartesianToMotorSteps(
        ReadOnlySpan<float> machinePos,
        ReadOnlySpan<float> stepsPerMm,
        int numVisibleAxes,
        int numTotalAxes,
        Span<int> motorPos,
        bool isCoordinated = false)
    {
        if (machinePos.Length < XyzAxes || motorPos.Length < XyzAxes || stepsPerMm.Length < XyzAxes)
        {
            return NativeMovementError.UnreachablePosition;
        }

        float theta, psi;
        if (machinePos[XAxis] == _cachedX && machinePos[YAxis] == _cachedY)
        {
            // Already solved, and reusing the answer keeps the arm mode stable at a point the caller
            // asked about twice - which is what a probe move does
            theta = _cachedTheta;
            psi = _cachedPsi;
            _currentArmMode = _cachedArmMode;
        }
        else
        {
            bool armMode = _currentArmMode;
            if (!TryCalculateThetaAndPsi(machinePos, isCoordinated, out theta, out psi, ref armMode))
            {
                return NativeMovementError.UnreachablePosition;
            }
            _currentArmMode = armMode;
        }

        NativeMovementError result = NativeMovementError.Ok;

        if (TryRoundToInt32(theta * stepsPerMm[XAxis], out int thetaSteps))
        {
            motorPos[XAxis] = thetaSteps;
        }
        else
        {
            result = NativeMovementError.MicrostepPositionTooLarge;
        }

        if (TryRoundToInt32((psi - (_crosstalk[0] * theta)) * stepsPerMm[YAxis], out int psiSteps))
        {
            motorPos[YAxis] = psiSteps;
        }
        else
        {
            result = NativeMovementError.MicrostepPositionTooLarge;
        }

        float z = machinePos[ZAxis] - (_crosstalk[1] * theta) - (_crosstalk[2] * psi);
        if (TryRoundToInt32(z * stepsPerMm[ZAxis], out int zSteps))
        {
            motorPos[ZAxis] = zSteps;
        }
        else
        {
            result = NativeMovementError.MicrostepPositionTooLarge;
        }

        NativeMovementError linearResult = LinearAxesToMotorSteps(machinePos, stepsPerMm, XyzAxes, numVisibleAxes, motorPos);
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
        if (machinePos.Length < XyzAxes || motorPos.Length < XyzAxes || stepsPerMm.Length < XyzAxes)
        {
            return;
        }

        float theta = motorPos[XAxis] / stepsPerMm[XAxis];
        float psi = (motorPos[YAxis] / stepsPerMm[YAxis]) + (_crosstalk[0] * theta);

        // A negative distal motor position means the elbow is bent the other way, which is the only
        // place the arm mode can be recovered from once the machine has moved on its own
        _currentArmMode = _cachedArmMode = motorPos[YAxis] >= 0;
        _cachedTheta = theta;
        _cachedPsi = psi;

        _cachedX = machinePos[XAxis] = (MathF.Cos(theta * DegreesToRadians) * ProximalArmLength)
                                       + (MathF.Cos((psi + theta) * DegreesToRadians) * DistalArmLength)
                                       - XOffset;
        _cachedY = machinePos[YAxis] = (MathF.Sin(theta * DegreesToRadians) * ProximalArmLength)
                                       + (MathF.Sin((psi + theta) * DegreesToRadians) * DistalArmLength)
                                       - YOffset;

        machinePos[ZAxis] = (motorPos[ZAxis] / stepsPerMm[ZAxis]) + (_crosstalk[1] * theta) + (_crosstalk[2] * psi);

        LinearMotorStepsToCartesian(motorPos, stepsPerMm, XyzAxes, numVisibleAxes, machinePos);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Both arm motors move for either X or Y. Z joins them only on a machine with arm-to-Z crosstalk,
    /// where turning an arm lifts the head as well
    /// </remarks>
    public override uint GetControllingDrives(int axis)
    {
        int numCoupledAxes = (_crosstalk[1] != 0.0f || _crosstalk[2] != 0.0f) ? 3 : 2;
        return (axis >= 0 && axis < numCoupledAxes) ? LowestDrives(numCoupledAxes) : base.GetControllingDrives(axis);
    }

    /// <summary>
    /// Whether the head can be put at the given XY position
    /// </summary>
    /// <param name="x">X coordinate in mm</param>
    /// <param name="y">Y coordinate in mm</param>
    /// <returns>True if it is within the annulus the arms sweep</returns>
    public bool IsReachable(float x, float y)
    {
        float dx = x + XOffset, dy = y + YOffset;
        float radiusSquared = (dx * dx) + (dy * dy);
        return radiusSquared >= MinRadius * MinRadius && radiusSquared <= MaxRadius * MaxRadius;
    }

    /// <summary>
    /// Work out the two joint angles that put the head at the given position
    /// </summary>
    /// <param name="machinePos">Axis coordinates in mm</param>
    /// <param name="isCoordinated">Whether the arm mode must be left alone</param>
    /// <param name="theta">Proximal joint angle in degrees</param>
    /// <param name="psi">Distal joint angle in degrees</param>
    /// <param name="armMode">Which way the elbow is bent, updated if it had to change</param>
    /// <returns>False if the position is out of reach in an allowed pose</returns>
    private bool TryCalculateThetaAndPsi(ReadOnlySpan<float> machinePos, bool isCoordinated, out float theta, out float psi, ref bool armMode)
    {
        theta = float.NaN;

        float x = machinePos[XAxis] + XOffset;
        float y = machinePos[YAxis] + YOffset;

        // The cosine rule on the triangle pillar-elbow-head gives the distal joint angle directly
        float cosPsi = ((x * x) + (y * y) - _proximalArmLengthSquared - _distalArmLengthSquared) / _twoPd;

        // Near +/-1 the arms are nearly in line and the pose is ill-conditioned: a tiny change in
        // position needs a huge change in angle, so the machine is kept away from there entirely
        float square = 1.0f - (cosPsi * cosPsi);
        if (square < 0.01f)
        {
            theta = psi = float.NaN;
            return false;
        }

        psi = MathF.Acos(cosPsi) * RadiansToDegrees;
        float sinPsi = MathF.Sqrt(square);
        float k1 = ProximalArmLength + (DistalArmLength * cosPsi);
        float k2 = DistalArmLength * sinPsi;

        // Try the mode the arm is already in, then the other one
        bool switchedMode = false;
        for (; ; )
        {
            if (armMode != switchedMode)
            {
                // Elbow anticlockwise relative to the proximal arm
                if (_supportsContinuousRotation[1] || (psi >= _psiLimits[0] && psi <= _psiLimits[1]))
                {
                    theta = MathF.Atan2((k1 * y) - (k2 * x), (k1 * x) + (k2 * y)) * RadiansToDegrees;
                    if (_supportsContinuousRotation[0] || (theta >= _thetaLimits[0] && theta <= _thetaLimits[1]))
                    {
                        break;
                    }
                }
            }
            else
            {
                // Elbow clockwise relative to the proximal arm
                if (_supportsContinuousRotation[1] || (-psi >= _psiLimits[0] && -psi <= _psiLimits[1]))
                {
                    theta = MathF.Atan2((k1 * y) + (k2 * x), (k1 * x) - (k2 * y)) * RadiansToDegrees;
                    if (_supportsContinuousRotation[0] || (theta >= _thetaLimits[0] && theta <= _thetaLimits[1]))
                    {
                        psi = -psi;
                        break;
                    }
                }
            }

            if (isCoordinated || switchedMode)
            {
                // Switching mode is a move in itself, so a coordinated move may not do it - and if the
                // other mode has already been tried there is nowhere left to look
                theta = psi = float.NaN;
                return false;
            }
            switchedMode = true;
        }

        if (switchedMode)
        {
            armMode = !armMode;
        }

        // Remember the answer so that being asked about the same point again gives the same pose
        _cachedX = machinePos[XAxis];
        _cachedY = machinePos[YAxis];
        _cachedTheta = theta;
        _cachedPsi = psi;
        _cachedArmMode = armMode;
        return true;
    }
}
