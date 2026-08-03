using System;
using DuetControlServer.Link.Native;

namespace DuetControlServer.Motion.Kinematics;

/// <summary>
/// The five-bar parallel SCARA geometry: two driven arms meeting at a shared joint
/// </summary>
/// <remarks>
/// <para>
/// Ported from RepRapFirmware's <c>FiveBarScaraKinematics</c>. Two motors sit at fixed points and each
/// swings a proximal arm; a distal arm runs from the end of each proximal arm and the two meet at a
/// common joint. Counting the bed as the fifth bar, that is a five-bar linkage, and unlike a serial
/// SCARA both motors are on the frame rather than one riding on the other. Steps per mm on X and Y are
/// steps per degree of the two actuator angles.
/// </para>
/// <para>
/// The head is either at that shared joint, or on a cantilever - an extension of one of the distal
/// arms past the joint. Both transforms are circle intersections: the inverse one intersects the
/// proximal arm's circle with the distal arm's circle about the target, the forward one intersects the
/// two distal circles about the swung arm ends. Each has two solutions, and which one is right is the
/// work mode: whether each elbow buckles inwards or bulges outwards.
/// </para>
/// <para>
/// A linkage like this jams: there are poses where the arms are in line and the mechanism can no
/// longer be steered, and poses that would need a link to pass through another. The angle limits are
/// what keeps it out of them, so a position that violates one is unreachable rather than merely
/// outside the bed
/// </para>
/// </remarks>
internal sealed class FiveBarScaraKinematicsEngine : KinematicsEngine
{
    private const int XAxis = 0, YAxis = 1, ZAxis = 2;
    private const int XyzAxes = 3;

    private const float DegreesToRadians = MathF.PI / 180.0f;

    /// <summary>Which of the two arms is being solved for</summary>
    private enum Arm
    {
        Left,
        Right
    }

    // Where the actuators are, mm
    private readonly float _xOrigL, _yOrigL, _xOrigR, _yOrigR;

    // Arm lengths, mm. cantL and cantR are how far the head is past the shared joint, zero if it is at it
    private readonly float _proximalL, _proximalR, _distalL, _distalR, _cantL, _cantR;

    // Angle limits, degrees
    private readonly float _headAngleMin, _headAngleMax;
    private readonly float _proxDistLAngleMin, _proxDistLAngleMax;
    private readonly float _proxDistRAngleMin, _proxDistRAngleMax;
    private readonly float _actuatorAngleLMin, _actuatorAngleLMax;
    private readonly float _actuatorAngleRMin, _actuatorAngleRMax;

    // The last solved pose. Mutable because the solve is expensive and the caller asks about the same
    // point more than once; the builder that calls this is single-threaded, as RepRapFirmware's is
    private float _cachedX0 = float.NaN, _cachedY0 = float.NaN;
    private float _cachedThetaL, _cachedThetaR;
    private float _cachedXL, _cachedYL, _cachedXR, _cachedYR;
    private float _cachedX1, _cachedY1;
    private bool _cachedInvalid = true;

    /// <summary>
    /// Which way each elbow bends: 1 is left buckled and right bulged, 2 both bulged, 4 both buckled
    /// </summary>
    public int WorkMode { get; }

    /// <inheritdoc />
    public override string Name => "FiveBarScara";

    /// <inheritdoc />
    /// <remarks>Both actuators turn about a fixed point with nothing to stop them going round</remarks>
    public override uint ContinuousRotationAxes => (1u << XAxis) | (1u << YAxis);

    /// <summary>
    /// Create a five-bar parallel SCARA geometry
    /// </summary>
    /// <param name="xOrigL">X coordinate of the left actuator, mm</param>
    /// <param name="yOrigL">Y coordinate of the left actuator, mm</param>
    /// <param name="xOrigR">X coordinate of the right actuator, mm</param>
    /// <param name="yOrigR">Y coordinate of the right actuator, mm</param>
    /// <param name="proximalL">Length of the left proximal arm, mm</param>
    /// <param name="proximalR">Length of the right proximal arm, mm</param>
    /// <param name="distalL">Length of the left distal arm, mm</param>
    /// <param name="distalR">Length of the right distal arm, mm</param>
    /// <param name="cantL">How far the head is past the joint on the left distal arm, mm</param>
    /// <param name="cantR">How far the head is past the joint on the right distal arm, mm</param>
    /// <param name="workMode">Which way each elbow bends: 1, 2 or 4</param>
    /// <param name="headAngles">Range the angle at the shared joint may take, degrees</param>
    /// <param name="proxDistLAngles">Range the left proximal-to-distal angle may take, degrees</param>
    /// <param name="proxDistRAngles">Range the right proximal-to-distal angle may take, degrees</param>
    /// <param name="actuatorLAngles">Range the left actuator may turn through, degrees</param>
    /// <param name="actuatorRAngles">Range the right actuator may turn through, degrees</param>
    public FiveBarScaraKinematicsEngine(
        float xOrigL, float yOrigL, float xOrigR, float yOrigR,
        float proximalL, float proximalR,
        float distalL, float distalR,
        float cantL = 0.0f, float cantR = 0.0f,
        int workMode = 1,
        ReadOnlySpan<float> headAngles = default,
        ReadOnlySpan<float> proxDistLAngles = default,
        ReadOnlySpan<float> proxDistRAngles = default,
        ReadOnlySpan<float> actuatorLAngles = default,
        ReadOnlySpan<float> actuatorRAngles = default)
    {
        _xOrigL = xOrigL;
        _yOrigL = yOrigL;
        _xOrigR = xOrigR;
        _yOrigR = yOrigR;
        _proximalL = proximalL;
        _proximalR = proximalR;
        _distalL = distalL;
        _distalR = distalR;
        _cantL = cantL;
        _cantR = cantR;
        WorkMode = (workMode == 1 || workMode == 2 || workMode == 4) ? workMode : 1;

        _headAngleMin = headAngles.Length > 0 ? headAngles[0] : 15.0f;
        _headAngleMax = headAngles.Length > 1 ? headAngles[1] : 165.0f;
        _proxDistLAngleMin = proxDistLAngles.Length > 0 ? proxDistLAngles[0] : 0.0f;
        _proxDistLAngleMax = proxDistLAngles.Length > 1 ? proxDistLAngles[1] : 360.0f;
        _proxDistRAngleMin = proxDistRAngles.Length > 0 ? proxDistRAngles[0] : 0.0f;
        _proxDistRAngleMax = proxDistRAngles.Length > 1 ? proxDistRAngles[1] : 360.0f;
        _actuatorAngleLMin = actuatorLAngles.Length > 0 ? actuatorLAngles[0] : 10.0f;
        _actuatorAngleLMax = actuatorLAngles.Length > 1 ? actuatorLAngles[1] : 170.0f;
        _actuatorAngleRMin = actuatorRAngles.Length > 0 ? actuatorRAngles[0] : 10.0f;
        _actuatorAngleRMax = actuatorRAngles.Length > 1 ? actuatorRAngles[1] : 170.0f;
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
        if (machinePos.Length < XyzAxes || motorPos.Length < XyzAxes || stepsPerMm.Length < XyzAxes)
        {
            return NativeMovementError.UnreachablePosition;
        }

        if (!ConstraintsOk(machinePos[XAxis], machinePos[YAxis]))
        {
            return NativeMovementError.UnreachablePosition;
        }

        NativeMovementError result = NativeMovementError.Ok;

        if (TryRoundToInt32(_cachedThetaL * stepsPerMm[XAxis], out int leftSteps))
        {
            motorPos[XAxis] = leftSteps;
        }
        else
        {
            result = NativeMovementError.MicrostepPositionTooLarge;
        }

        if (TryRoundToInt32(_cachedThetaR * stepsPerMm[YAxis], out int rightSteps))
        {
            motorPos[YAxis] = rightSteps;
        }
        else
        {
            result = NativeMovementError.MicrostepPositionTooLarge;
        }

        // Z and anything above it have a motor each
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
        if (machinePos.Length < XyzAxes || motorPos.Length < XyzAxes || stepsPerMm.Length < XyzAxes)
        {
            return;
        }

        float thetaL = motorPos[XAxis] / stepsPerMm[XAxis];
        float thetaR = motorPos[YAxis] / stepsPerMm[YAxis];

        Forward(thetaL, thetaR, out float xL, out float yL, out float xR, out float yR, out float x1, out float y1);

        float x0, y0;
        if (IsCantilevered(1))
        {
            // The head is out past the shared joint along the left distal arm, so it carries on in the
            // direction that arm already points
            float psiL = AbsoluteAngle(xL, yL, x1, y1);
            (x0, y0) = FromAngle(psiL, _cantL, x1, y1);
        }
        else if (IsCantilevered(2))
        {
            float psiR = AbsoluteAngle(xR, yR, x1, y1);
            (x0, y0) = FromAngle(psiR, _cantR, x1, y1);
        }
        else
        {
            x0 = x1;
            y0 = y1;
        }

        machinePos[XAxis] = x0;
        machinePos[YAxis] = y0;
        machinePos[ZAxis] = motorPos[ZAxis] / stepsPerMm[ZAxis];

        LinearMotorStepsToCartesian(motorPos, stepsPerMm, XyzAxes, numVisibleAxes, machinePos);
    }

    /// <inheritdoc />
    /// <remarks>Both actuators move for either X or Y; neither maps onto one of them</remarks>
    public override uint GetControllingDrives(int axis)
        => (axis == XAxis || axis == YAxis) ? LowestDrives(2) : base.GetControllingDrives(axis);

    /// <summary>
    /// Whether the head can be put at the given XY position without violating a joint limit
    /// </summary>
    /// <param name="x">X coordinate in mm</param>
    /// <param name="y">Y coordinate in mm</param>
    /// <returns>True if the linkage can hold that pose</returns>
    public bool IsReachable(float x, float y) => ConstraintsOk(x, y);

    /// <summary>The angle the left actuator was last solved to, degrees</summary>
    public float ThetaLeft => _cachedThetaL;

    /// <summary>The angle the right actuator was last solved to, degrees</summary>
    public float ThetaRight => _cachedThetaR;

    /// <summary>
    /// Whether the head hangs off the end of a distal arm rather than sitting at the shared joint
    /// </summary>
    /// <param name="mode">1 for the left arm, 2 for the right</param>
    /// <returns>True if that arm is cantilevered</returns>
    private bool IsCantilevered(int mode) => (_cantL > 0.0f && mode == 1) || (_cantR > 0.0f && mode == 2);

    /// <summary>
    /// Solve the linkage for a head position, filling in the cache
    /// </summary>
    /// <param name="x0">X coordinate of the head in mm</param>
    /// <param name="y0">Y coordinate of the head in mm</param>
    /// <remarks>
    /// The cantilevered arm has to be solved first, because only it runs all the way to the head; the
    /// shared joint is then a known fraction of the way along it, and the other arm is solved to that
    /// </remarks>
    private void Inverse(float x0, float y0)
    {
        if (!_cachedInvalid && x0 == _cachedX0 && y0 == _cachedY0)
        {
            return;
        }

        float thetaL, thetaR, xL, yL, xR, yR, x1, y1;

        if (IsCantilevered(1))
        {
            (xL, yL, thetaL) = Theta(_proximalL, _distalL + _cantL, _xOrigL, _yOrigL, x0, y0, Arm.Left);

            float fraction = _distalL / (_distalL + _cantL);
            x1 = ((x0 - xL) * fraction) + xL;
            y1 = ((y0 - yL) * fraction) + yL;

            (xR, yR, thetaR) = Theta(_proximalR, _distalR, _xOrigR, _yOrigR, x1, y1, Arm.Right);
        }
        else if (IsCantilevered(2))
        {
            (xR, yR, thetaR) = Theta(_proximalR, _distalR + _cantR, _xOrigR, _yOrigR, x0, y0, Arm.Right);

            float fraction = _distalR / (_distalR + _cantR);
            x1 = ((x0 - xR) * fraction) + xR;
            y1 = ((y0 - yR) * fraction) + yR;

            (xL, yL, thetaL) = Theta(_proximalL, _distalL, _xOrigL, _yOrigL, x1, y1, Arm.Left);
        }
        else
        {
            // The head is the shared joint, so both arms are solved straight to it
            (xL, yL, thetaL) = Theta(_proximalL, _distalL, _xOrigL, _yOrigL, x0, y0, Arm.Left);
            (xR, yR, thetaR) = Theta(_proximalR, _distalR, _xOrigR, _yOrigR, x0, y0, Arm.Right);
            x1 = x0;
            y1 = y0;
        }

        _cachedX0 = x0;
        _cachedY0 = y0;
        _cachedXL = xL;
        _cachedYL = yL;
        _cachedThetaL = thetaL;
        _cachedXR = xR;
        _cachedYR = yR;
        _cachedThetaR = thetaR;
        _cachedX1 = x1;
        _cachedY1 = y1;

        _cachedInvalid = float.IsNaN(x0) || float.IsNaN(y0)
                         || float.IsNaN(x1) || float.IsNaN(y1)
                         || float.IsNaN(xL) || float.IsNaN(yL) || float.IsNaN(thetaL)
                         || float.IsNaN(xR) || float.IsNaN(yR) || float.IsNaN(thetaR);
    }

    /// <summary>
    /// Solve the linkage and check every joint limit
    /// </summary>
    /// <param name="x0">X coordinate of the head in mm</param>
    /// <param name="y0">Y coordinate of the head in mm</param>
    /// <returns>True if the pose exists and is allowed</returns>
    private bool ConstraintsOk(float x0, float y0)
    {
        if (!_cachedInvalid && x0 == _cachedX0 && y0 == _cachedY0)
        {
            return true;
        }

        Inverse(x0, y0);
        if (_cachedInvalid)
        {
            return false;
        }

        // A negative minimum means the range straddles zero, so an angle just above it comes back as
        // just under 360 and has to be brought back down before it can be compared
        float thetaL = _cachedThetaL;
        if (_actuatorAngleLMin < 0.0f && thetaL > _actuatorAngleLMax)
        {
            thetaL -= 360.0f;
        }
        if (thetaL < _actuatorAngleLMin || thetaL > _actuatorAngleLMax)
        {
            _cachedInvalid = true;
            return false;
        }

        float thetaR = _cachedThetaR;
        if (_actuatorAngleRMin < 0.0f && thetaR > _actuatorAngleRMax)
        {
            thetaR -= 360.0f;
        }
        if (thetaR < _actuatorAngleRMin || thetaR > _actuatorAngleRMax)
        {
            _cachedInvalid = true;
            return false;
        }

        // The angle at the shared joint. Too small and the two distal arms are nearly on top of each
        // other, which is where the linkage loses control of the head
        float headAngle = Angle(_cachedXL, _cachedYL, _cachedX1, _cachedY1, _cachedXR, _cachedYR);
        if (float.IsNaN(headAngle) || headAngle < _headAngleMin || headAngle > _headAngleMax)
        {
            _cachedInvalid = true;
            return false;
        }

        float angleProxDistL = Angle(_xOrigL, _yOrigL, _cachedXL, _cachedYL, _cachedX1, _cachedY1);
        if (float.IsNaN(angleProxDistL) || angleProxDistL < _proxDistLAngleMin || angleProxDistL > _proxDistLAngleMax)
        {
            _cachedInvalid = true;
            return false;
        }

        float angleProxDistR = Angle(_xOrigR, _yOrigR, _cachedXR, _cachedYR, _cachedX1, _cachedY1);
        if (float.IsNaN(angleProxDistR) || angleProxDistR < _proxDistRAngleMin || angleProxDistR > _proxDistRAngleMax)
        {
            _cachedInvalid = true;
            return false;
        }

        _cachedInvalid = false;
        return true;
    }

    /// <summary>
    /// Find the effector position from two actuator angles
    /// </summary>
    /// <param name="thetaL">Left actuator angle, degrees</param>
    /// <param name="thetaR">Right actuator angle, degrees</param>
    /// <param name="xL">X of the left elbow, mm</param>
    /// <param name="yL">Y of the left elbow, mm</param>
    /// <param name="xR">X of the right elbow, mm</param>
    /// <param name="yR">Y of the right elbow, mm</param>
    /// <param name="x1">X of the shared joint, mm, or NaN if the pose is not in the work mode</param>
    /// <param name="y1">Y of the shared joint, mm, or NaN if the pose is not in the work mode</param>
    private void Forward(float thetaL, float thetaR, out float xL, out float yL, out float xR, out float yR, out float x1, out float y1)
    {
        (xL, yL) = FromAngle(thetaL, _proximalL, _xOrigL, _yOrigL);
        (xR, yR) = FromAngle(thetaR, _proximalR, _xOrigR, _yOrigR);

        Intersect(_distalL, _distalR, xL, yL, xR, yR, out float ix1, out float iy1, out float ix2, out float iy2);

        // Two circles cross twice, and the shared joint is whichever crossing has the linkage folded
        // the way this work mode says
        float turnHot0 = Turn(xL, yL, ix1, iy1, xR, yR);
        float turnHot1 = Turn(xL, yL, ix2, iy2, xR, yR);

        x1 = float.NaN;
        y1 = float.NaN;
        if (turnHot0 < 0.0f)
        {
            x1 = ix1;
            y1 = iy1;
        }
        else if (turnHot1 < 0.0f)
        {
            x1 = ix2;
            y1 = iy2;
        }

        // The elbows have to buckle the way the work mode says as well, or this is a different pose of
        // the same linkage that the motors did not actually reach
        float tL = Turn(_xOrigL, _yOrigL, xL, yL, x1, y1);
        float tR = Turn(_xOrigR, _yOrigR, xR, yR, x1, y1);
        if ((WorkMode == 1 && (tL < 0.0f || tR < 0.0f))
            || (WorkMode == 2 && (tL > 0.0f || tR < 0.0f))
            || (WorkMode == 3 && (tL < 0.0f || tR > 0.0f))
            || (WorkMode == 4 && (tL > 0.0f || tR > 0.0f)))
        {
            x1 = float.NaN;
            y1 = float.NaN;
        }
    }

    /// <summary>
    /// Solve one arm: where its elbow is and what angle its actuator is at
    /// </summary>
    /// <param name="proximal">Proximal arm length, mm</param>
    /// <param name="distal">Distal arm length, mm</param>
    /// <param name="proxX">X of the actuator, mm</param>
    /// <param name="proxY">Y of the actuator, mm</param>
    /// <param name="destX">X the distal arm must reach, mm</param>
    /// <param name="destY">Y the distal arm must reach, mm</param>
    /// <param name="arm">Which arm this is</param>
    /// <returns>Elbow position and actuator angle, all NaN if no solution suits the work mode</returns>
    private (float X, float Y, float Theta) Theta(float proximal, float distal, float proxX, float proxY, float destX, float destY, Arm arm)
    {
        Intersect(proximal, distal, proxX, proxY, destX, destY, out float x1, out float y1, out float x2, out float y2);

        float thetaA = AbsoluteAngle(proxX, proxY, x1, y1);
        float thetaB = AbsoluteAngle(proxX, proxY, x2, y2);

        // Which side of the proximal arm the target lies on is exactly the elbow's buckle direction
        float proxTurnA = Turn(proxX, proxY, x1, y1, destX, destY);
        float proxTurnB = Turn(proxX, proxY, x2, y2, destX, destY);

        // Work mode 1 buckles both arms one way, mode 4 both the other way, and mode 2 one of each
        bool wantPositiveTurn = WorkMode switch
        {
            1 => true,
            2 => arm == Arm.Right,
            3 => arm == Arm.Left,
            _ => false
        };

        bool aFits = wantPositiveTurn ? proxTurnA > 0.0f : proxTurnA < 0.0f;
        bool bFits = wantPositiveTurn ? proxTurnB > 0.0f : proxTurnB < 0.0f;

        return aFits ? (x1, y1, thetaA)
             : bFits ? (x2, y2, thetaB)
             : (float.NaN, float.NaN, float.NaN);
    }

    /// <summary>
    /// Where two circles cross
    /// </summary>
    /// <param name="firstRadius">Radius of the first circle, mm</param>
    /// <param name="secondRadius">Radius of the second circle, mm</param>
    /// <param name="firstX">X of the first centre, mm</param>
    /// <param name="firstY">Y of the first centre, mm</param>
    /// <param name="secondX">X of the second centre, mm</param>
    /// <param name="secondY">Y of the second centre, mm</param>
    /// <param name="x1">X of the first crossing</param>
    /// <param name="y1">Y of the first crossing</param>
    /// <param name="x2">X of the second crossing</param>
    /// <param name="y2">Y of the second crossing</param>
    /// <remarks>
    /// Both come out NaN if the circles do not meet, which is the arms being too short to span the gap
    /// or one being inside the other. The delta term is Heron's formula, so it goes imaginary exactly
    /// when the three lengths cannot make a triangle
    /// </remarks>
    private static void Intersect(
        float firstRadius, float secondRadius,
        float firstX, float firstY, float secondX, float secondY,
        out float x1, out float y1, out float x2, out float y2)
    {
        float firstRadius2 = firstRadius * firstRadius;
        float secondRadius2 = secondRadius * secondRadius;

        float distance2 = ((firstX - secondX) * (firstX - secondX)) + ((firstY - secondY) * (firstY - secondY));
        float distance = MathF.Sqrt(distance2);

        float delta = 0.25f * MathF.Sqrt(
            (distance + firstRadius + secondRadius)
            * (distance + firstRadius - secondRadius)
            * (distance - firstRadius + secondRadius)
            * (-distance + firstRadius + secondRadius));

        float term1X = (firstX + secondX) / 2.0f;
        float term2X = (secondX - firstX) * (firstRadius2 - secondRadius2) / (2.0f * distance2);
        float term3X = 2.0f * (firstY - secondY) / distance2 * delta;
        x1 = term1X + term2X + term3X;
        x2 = term1X + term2X - term3X;

        float term1Y = (firstY + secondY) / 2.0f;
        float term2Y = (secondY - firstY) * (firstRadius2 - secondRadius2) / (2.0f * distance2);
        float term3Y = 2.0f * (firstX - secondX) / distance2 * delta;
        y1 = term1Y + term2Y - term3Y;
        y2 = term1Y + term2Y + term3Y;
    }

    /// <summary>
    /// The direction from one point to another, measured anticlockwise from +X
    /// </summary>
    /// <param name="xOrig">X of the origin, mm</param>
    /// <param name="yOrig">Y of the origin, mm</param>
    /// <param name="xDest">X of the destination, mm</param>
    /// <param name="yDest">Y of the destination, mm</param>
    /// <returns>The angle in degrees, between -90 and 270</returns>
    private static float AbsoluteAngle(float xOrig, float yOrig, float xDest, float yDest)
    {
        float length = MathF.Sqrt(((xOrig - xDest) * (xOrig - xDest)) + ((yOrig - yDest) * (yOrig - yDest)));
        float y = MathF.Abs(yOrig - yDest);
        float angle = MathF.Asin(y / length) * 180.0f / MathF.PI;

        // Arc sine only distinguishes above from below, so the quadrant has to be put back by hand
        float dx = xDest - xOrig, dy = yDest - yOrig;
        return (dx >= 0.0f && dy >= 0.0f) ? angle
             : (dx < 0.0f && dy >= 0.0f) ? 180.0f - angle
             : (dx < 0.0f && dy < 0.0f) ? 180.0f + angle
             : 360.0f - angle;
    }

    /// <summary>
    /// Where you get to by going a given distance in a given direction
    /// </summary>
    /// <param name="angle">Direction in degrees</param>
    /// <param name="length">Distance in mm</param>
    /// <param name="origX">X to start from, mm</param>
    /// <param name="origY">Y to start from, mm</param>
    /// <returns>The destination</returns>
    private static (float X, float Y) FromAngle(float angle, float length, float origX, float origY)
        => ((length * MathF.Cos(angle * DegreesToRadians)) + origX,
            (length * MathF.Sin(angle * DegreesToRadians)) + origY);

    /// <summary>
    /// The angle at the middle point of three, measured clockwise from the third to the first
    /// </summary>
    /// <param name="x1">X of the first point, mm</param>
    /// <param name="y1">Y of the first point, mm</param>
    /// <param name="x2">X of the middle point, mm</param>
    /// <param name="y2">Y of the middle point, mm</param>
    /// <param name="x3">X of the third point, mm</param>
    /// <param name="y3">Y of the third point, mm</param>
    /// <returns>The angle in degrees, always positive</returns>
    private static float Angle(float x1, float y1, float x2, float y2, float x3, float y3)
    {
        float angle1 = AbsoluteAngle(x2, y2, x1, y1);
        float angle2 = AbsoluteAngle(x2, y2, x3, y3);
        return (angle2 < angle1) ? 360.0f + angle2 - angle1 : angle2 - angle1;
    }

    /// <summary>
    /// Which way a path through three points turns
    /// </summary>
    /// <param name="x1">X of the first point, mm</param>
    /// <param name="y1">Y of the first point, mm</param>
    /// <param name="x2">X of the second point, mm</param>
    /// <param name="y2">Y of the second point, mm</param>
    /// <param name="x3">X of the third point, mm</param>
    /// <param name="y3">Y of the third point, mm</param>
    /// <returns>Positive for anticlockwise, negative for clockwise</returns>
    /// <remarks>Twice the signed area of the triangle, which is the cross product of the two legs</remarks>
    private static float Turn(float x1, float y1, float x2, float y2, float x3, float y3)
        => ((x2 - x1) * (y3 - y1)) - ((y2 - y1) * (x3 - x1));

    /// <summary>
    /// Which macro to run next to home some of a set of axes
    /// </summary>
    /// <param name="toBeHomed">Axes still to home, as a bitmap</param>
    /// <param name="alreadyHomed">Axes already homed, as a bitmap</param>
    /// <param name="axisLetters">Letter of each axis, in axis order</param>
    /// <param name="fileName">The macro to run</param>
    /// <returns>Axes that have to be homed first</returns>
    /// <remarks>
    /// The two arms are linked through the bar between them, so neither can be homed on its own. One
    /// macro homes the mechanism however much of it was asked for
    /// </remarks>
    public override uint GetHomingFileName(uint toBeHomed, uint alreadyHomed, ReadOnlySpan<char> axisLetters,
                                           out string fileName)
    {
        uint mustHomeFirst = base.GetHomingFileName(toBeHomed, alreadyHomed, axisLetters, out fileName);
        fileName = "home5barscara.g";
        return mustHomeFirst;
    }
}
