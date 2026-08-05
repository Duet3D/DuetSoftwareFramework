using System;
using DuetControlServer.Link.Native;

namespace DuetControlServer.Motion.Kinematics;

/// <summary>
/// The hangprinter geometry: an effector suspended on lines from anchors around and above the workspace
/// </summary>
/// <remarks>
/// <para>
/// Ported from RepRapFirmware's <c>HangprinterKinematics</c>. There are no rails at all - each motor
/// winds a line to an anchor point, and the effector hangs where the line lengths put it. The inverse
/// transform is therefore trivial: the length of each line is the distance from the effector to that
/// anchor. What the motors actually count is how much line has been paid out since the origin, so the
/// distance at the origin is subtracted off.
/// </para>
/// <para>
/// The forward transform has no closed form. Four or more distances over-determine three unknowns, so
/// it is solved as a least squares problem: RepRapFirmware uses a Halley-accelerated Gauss-Newton with
/// a Levenberg damping term, falling back to plain damped Gauss-Newton once the cheap quadratic
/// correction has stopped paying for itself. If it does not converge the position is left alone rather
/// than being set to a guess, because a wrong answer here is a machine that thinks it is somewhere it
/// is not.
/// </para>
/// <para>
/// Two of RepRapFirmware's refinements are not ported, because nothing in the object model can express
/// them and so nothing could configure them. The first is line buildup compensation, where the
/// effective spool radius grows as line winds onto it and steps per mm therefore vary with position;
/// this engine uses the constant-radius model, which is the branch RepRapFirmware itself takes when
/// the buildup factor is zero. The second is flex compensation, which needs the mover's weight and the
/// lines' spring constants; RepRapFirmware leaves it off unless it has been given those
/// </para>
/// </remarks>
internal sealed class HangprinterKinematicsEngine : KinematicsEngine
{
    /// <summary>Most anchors a hangprinter may have</summary>
    public const int MaxAnchors = 8;

    /// <summary>Anchors a hangprinter has unless M669 says otherwise</summary>
    public const int DefaultNumAnchors = 4;

    /// <summary>Print radius RepRapFirmware assumes until M669 says otherwise, mm</summary>
    public const float DefaultPrintRadius = 1500.0f;

    // Solver settings, from RepRapFirmware's call to SolveHybrid
    private const float SolverDamping = 1.0e-3f;
    private const float SolverTolerance = 1.0e-3f;
    private const int SolverHalleyIterations = 3;
    private const int SolverMaxIterations = 30;

    /// <summary>Largest residual cost an answer may have and still be believed</summary>
    private const float SolverMaxCost = 10.0f;

    /// <summary>Where each anchor is, mm</summary>
    private readonly float[,] _anchors = new float[MaxAnchors, 3];

    /// <summary>How long each line is when the effector is at the origin, mm</summary>
    private readonly float[] _distancesOrigin = new float[MaxAnchors];

    /// <summary>Number of anchors, which is the number of line motors</summary>
    public int NumAnchors { get; }

    /// <summary>How far from the centre the effector may go, mm</summary>
    public float PrintRadius { get; }

    /// <inheritdoc />
    public override string Name => "Hangprinter";

    /// <inheritdoc />
    /// <remarks>Each motor has its own endstop, so a homing move addresses the motors directly</remarks>
    public override bool HomesIndividualDrives => true;

    /// <summary>
    /// Create a hangprinter geometry
    /// </summary>
    /// <param name="anchors">Anchor positions, each an X, Y and Z in mm</param>
    /// <param name="printRadius">How far from the centre the effector may go, mm</param>
    public HangprinterKinematicsEngine(ReadOnlySpan<float[]> anchors, float printRadius = DefaultPrintRadius)
    {
        NumAnchors = Math.Clamp(anchors.Length, 3, MaxAnchors);
        PrintRadius = printRadius;

        for (int anchor = 0; anchor < NumAnchors; anchor++)
        {
            float[] position = anchors[anchor];
            for (int axis = 0; axis < 3; axis++)
            {
                _anchors[anchor, axis] = axis < position.Length ? position[axis] : 0.0f;
            }

            // A line position is how much line has been paid out since the origin, not its length, so
            // the length at the origin is the offset between the two
            _distancesOrigin[anchor] = MathF.Sqrt(
                (_anchors[anchor, 0] * _anchors[anchor, 0])
                + (_anchors[anchor, 1] * _anchors[anchor, 1])
                + (_anchors[anchor, 2] * _anchors[anchor, 2]));
        }
    }

    /// <summary>
    /// A hangprinter with RepRapFirmware's default anchors, for before M669 has been seen
    /// </summary>
    /// <returns>The engine</returns>
    public static HangprinterKinematicsEngine CreateDefault()
        => new([[0.0f, -2000.0f, -100.0f], [2000.0f, 1000.0f, -100.0f], [-2000.0f, 1000.0f, -100.0f], [0.0f, 0.0f, 3000.0f]]);

    /// <summary>Where an anchor is, mm</summary>
    /// <param name="anchor">Anchor number</param>
    /// <param name="axis">0 for X, 1 for Y, 2 for Z</param>
    /// <returns>The coordinate</returns>
    public float GetAnchor(int anchor, int axis) => _anchors[anchor, axis];

    /// <summary>How long a line is when the effector is at the origin, mm</summary>
    /// <param name="anchor">Anchor number</param>
    /// <returns>The length</returns>
    public float GetDistanceAtOrigin(int anchor) => _distancesOrigin[anchor];

    /// <inheritdoc />
    /// <remarks>
    /// Only the line motors are converted. RepRapFirmware does the same: on a hangprinter the drives
    /// are lines rather than axes, so there is no drive left over for an extra axis to use
    /// </remarks>
    public override NativeMovementError CartesianToMotorSteps(
        ReadOnlySpan<float> machinePos,
        ReadOnlySpan<float> stepsPerMm,
        int numVisibleAxes,
        int numTotalAxes,
        Span<int> motorPos,
        bool isCoordinated = false)
    {
        if (machinePos.Length < 3)
        {
            return NativeMovementError.UnreachablePosition;
        }

        NativeMovementError result = NativeMovementError.Ok;
        int limit = Math.Min(NumAnchors, Math.Min(motorPos.Length, stepsPerMm.Length));

        for (int anchor = 0; anchor < limit; anchor++)
        {
            float linePos = DistanceToAnchor(machinePos[0], machinePos[1], machinePos[2], anchor) - _distancesOrigin[anchor];
            if (TryRoundToInt32(linePos * stepsPerMm[anchor], out int steps))
            {
                motorPos[anchor] = steps;
            }
            else
            {
                result = NativeMovementError.MicrostepPositionTooLarge;
            }
        }

        return result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The position is left as it was found if the solver does not settle on an answer it believes.
    /// The caller's previous idea of where the machine is may be stale, but it is not fabricated
    /// </remarks>
    public override void MotorStepsToCartesian(
        ReadOnlySpan<int> motorPos,
        ReadOnlySpan<float> stepsPerMm,
        int numVisibleAxes,
        int numTotalAxes,
        Span<float> machinePos)
    {
        if (machinePos.Length < 3 || motorPos.Length < NumAnchors || stepsPerMm.Length < NumAnchors)
        {
            return;
        }

        Span<float> linePositions = stackalloc float[MaxAnchors];
        for (int anchor = 0; anchor < NumAnchors; anchor++)
        {
            linePositions[anchor] = motorPos[anchor] / stepsPerMm[anchor];
        }

        if (TrySolve(linePositions, out float x, out float y, out float z))
        {
            machinePos[0] = x;
            machinePos[1] = y;
            machinePos[2] = z;
        }
    }

    /// <inheritdoc />
    /// <remarks>Every line takes part in holding the effector still, whichever axis is asked about</remarks>
    public override uint GetControllingDrives(int axis)
        => (axis >= 0 && axis < NumAnchors) ? LowestDrives(NumAnchors) : base.GetControllingDrives(axis);

    /// <summary>
    /// Whether the effector is inside the printable cylinder
    /// </summary>
    /// <param name="x">X coordinate in mm</param>
    /// <param name="y">Y coordinate in mm</param>
    /// <returns>True if it is within the print radius</returns>
    public bool IsReachable(float x, float y) => (x * x) + (y * y) <= PrintRadius * PrintRadius;

    /// <summary>
    /// How far a point is from an anchor
    /// </summary>
    /// <param name="x">X coordinate in mm</param>
    /// <param name="y">Y coordinate in mm</param>
    /// <param name="z">Z coordinate in mm</param>
    /// <param name="anchor">Anchor number</param>
    /// <returns>The distance in mm</returns>
    private float DistanceToAnchor(float x, float y, float z, int anchor)
    {
        float dx = x - _anchors[anchor, 0];
        float dy = y - _anchors[anchor, 1];
        float dz = z - _anchors[anchor, 2];
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    /// <summary>
    /// Find the effector position that best fits a set of line positions
    /// </summary>
    /// <param name="linePositions">How much line each motor has paid out since the origin, mm</param>
    /// <param name="x">X coordinate in mm</param>
    /// <param name="y">Y coordinate in mm</param>
    /// <param name="z">Z coordinate in mm</param>
    /// <returns>False if the solver did not settle on an answer worth believing</returns>
    /// <remarks>
    /// Gauss-Newton on the squared line length errors, damped so that a nearly singular pose does not
    /// throw the step out to infinity. The first few iterations add Halley's quadratic correction,
    /// which needs the second derivatives but roughly triples the order of convergence; once the
    /// iterate is close that correction costs more than it saves, so the rest are plain steps
    /// </remarks>
    private bool TrySolve(ReadOnlySpan<float> linePositions, out float x, out float y, out float z)
    {
        Span<float> position = stackalloc float[3];
        Span<float> residuals = stackalloc float[MaxAnchors];
        Span<float> jacobian = stackalloc float[MaxAnchors * 3];
        Span<float> hessians = stackalloc float[MaxAnchors * 9];
        Span<float> jacobianBar = stackalloc float[MaxAnchors * 3];
        Span<float> normalMatrix = stackalloc float[9];
        Span<float> gradient = stackalloc float[3];
        Span<float> delta = stackalloc float[3];

        position.Clear();
        bool converged = false;
        float cost;

        int iteration = 0;
        for (; iteration < SolverHalleyIterations && iteration < SolverMaxIterations; iteration++)
        {
            ComputeResiduals(linePositions, position, residuals, jacobian, hessians);
            AccumulateNormalSystem(jacobian, residuals, normalMatrix, gradient);

            if (!TrySolveNormalSystem(normalMatrix, gradient, delta))
            {
                break;
            }

            // Bend the Jacobian along the step the plain method would take. That is Halley's method:
            // a first-order correction for the curvature the step is about to run into
            for (int anchor = 0; anchor < NumAnchors; anchor++)
            {
                for (int column = 0; column < 3; column++)
                {
                    float curvature = (delta[0] * hessians[(anchor * 9) + column])
                                      + (delta[1] * hessians[(anchor * 9) + 3 + column])
                                      + (delta[2] * hessians[(anchor * 9) + 6 + column]);
                    jacobianBar[(anchor * 3) + column] = jacobian[(anchor * 3) + column] + (0.5f * curvature);
                }
            }

            AccumulateNormalSystem(jacobianBar, residuals, normalMatrix, gradient);
            if (!TrySolveNormalSystem(normalMatrix, gradient, delta))
            {
                break;
            }

            position[0] += delta[0];
            position[1] += delta[1];
            position[2] += delta[2];

            if (Norm(delta) < SolverTolerance)
            {
                converged = true;
                break;
            }
        }

        for (; iteration < SolverMaxIterations && !converged; iteration++)
        {
            ComputeResiduals(linePositions, position, residuals, jacobian, default);
            AccumulateNormalSystem(jacobian, residuals, normalMatrix, gradient);

            if (!TrySolveNormalSystem(normalMatrix, gradient, delta))
            {
                break;
            }

            position[0] += delta[0];
            position[1] += delta[1];
            position[2] += delta[2];

            if (Norm(delta) < SolverTolerance)
            {
                converged = true;
                break;
            }
        }

        cost = ComputeResiduals(linePositions, position, residuals, jacobian, default);

        x = position[0];
        y = position[1];
        z = position[2];

        // A converged solve that still does not fit means the line positions do not describe a point
        // this machine can be at, so the answer is a least-squares compromise rather than a position
        return converged && cost <= SolverMaxCost;
    }

    /// <summary>
    /// Work out how badly a candidate position fits the line positions, and which way to move
    /// </summary>
    /// <param name="linePositions">How much line each motor has paid out since the origin, mm</param>
    /// <param name="position">Candidate position</param>
    /// <param name="residuals">Filled in with the error for each line</param>
    /// <param name="jacobian">Filled in with the derivative of each error, three per line</param>
    /// <param name="hessians">Filled in with the second derivatives, nine per line; may be empty</param>
    /// <returns>Half the sum of the squared errors</returns>
    private float ComputeResiduals(
        ReadOnlySpan<float> linePositions,
        ReadOnlySpan<float> position,
        Span<float> residuals,
        Span<float> jacobian,
        Span<float> hessians)
    {
        float cost = 0.0f;
        Span<float> diff = stackalloc float[3];

        for (int anchor = 0; anchor < NumAnchors; anchor++)
        {
            float dx = position[0] - _anchors[anchor, 0];
            float dy = position[1] - _anchors[anchor, 1];
            float dz = position[2] - _anchors[anchor, 2];

            // Right on top of an anchor the derivative of distance is undefined, so the distance is
            // floored rather than letting the step become an infinity
            float distance = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            if (distance < 1.0e-6f)
            {
                distance = 1.0e-6f;
            }

            float invLen = 1.0f / distance;
            float invLen3 = invLen * invLen * invLen;

            residuals[anchor] = distance - _distancesOrigin[anchor] - linePositions[anchor];

            // The gradient of a distance is the unit vector towards the point
            jacobian[(anchor * 3) + 0] = dx * invLen;
            jacobian[(anchor * 3) + 1] = dy * invLen;
            jacobian[(anchor * 3) + 2] = dz * invLen;

            if (!hessians.IsEmpty)
            {
                diff[0] = dx;
                diff[1] = dy;
                diff[2] = dz;
                for (int row = 0; row < 3; row++)
                {
                    for (int column = 0; column < 3; column++)
                    {
                        float identity = (row == column) ? invLen : 0.0f;
                        hessians[(anchor * 9) + (row * 3) + column] = identity - (diff[row] * diff[column] * invLen3);
                    }
                }
            }

            cost += 0.5f * residuals[anchor] * residuals[anchor];
        }

        return cost;
    }

    /// <summary>
    /// Build the damped normal equations for one Gauss-Newton step
    /// </summary>
    /// <param name="jacobian">Derivative of each error, three per line</param>
    /// <param name="residuals">Error for each line</param>
    /// <param name="normalMatrix">Filled in with J transpose J plus the damping term</param>
    /// <param name="gradient">Filled in with J transpose r</param>
    private void AccumulateNormalSystem(
        ReadOnlySpan<float> jacobian,
        ReadOnlySpan<float> residuals,
        Span<float> normalMatrix,
        Span<float> gradient)
    {
        normalMatrix.Clear();
        gradient.Clear();

        for (int anchor = 0; anchor < NumAnchors; anchor++)
        {
            for (int row = 0; row < 3; row++)
            {
                gradient[row] += jacobian[(anchor * 3) + row] * residuals[anchor];
                for (int column = 0; column < 3; column++)
                {
                    normalMatrix[(row * 3) + column] += jacobian[(anchor * 3) + row] * jacobian[(anchor * 3) + column];
                }
            }
        }

        // Levenberg damping. Without it a pose where two lines pull the same way leaves the matrix
        // nearly singular, and the step it produces overshoots wildly
        normalMatrix[0] += SolverDamping;
        normalMatrix[4] += SolverDamping;
        normalMatrix[8] += SolverDamping;
    }

    /// <summary>
    /// Solve the three by three normal equations for the step to take
    /// </summary>
    /// <param name="normalMatrix">J transpose J plus damping</param>
    /// <param name="gradient">J transpose r</param>
    /// <param name="delta">Filled in with the step, which is the solution negated</param>
    /// <returns>False if the system is singular</returns>
    private static bool TrySolveNormalSystem(ReadOnlySpan<float> normalMatrix, ReadOnlySpan<float> gradient, Span<float> delta)
    {
        // The augmented matrix, three rows of the system and the right hand side
        Span<float> system = stackalloc float[12];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                system[(row * 4) + column] = normalMatrix[(row * 3) + column];
            }
            system[(row * 4) + 3] = -gradient[row];
        }

        for (int i = 0; i < 3; i++)
        {
            int pivot = i;
            for (int row = i + 1; row < 3; row++)
            {
                if (MathF.Abs(system[(row * 4) + i]) > MathF.Abs(system[(pivot * 4) + i]))
                {
                    pivot = row;
                }
            }

            if (MathF.Abs(system[(pivot * 4) + i]) < 1.0e-12f)
            {
                return false;
            }

            if (pivot != i)
            {
                for (int column = 0; column < 4; column++)
                {
                    (system[(i * 4) + column], system[(pivot * 4) + column]) =
                        (system[(pivot * 4) + column], system[(i * 4) + column]);
                }
            }

            float scale = 1.0f / system[(i * 4) + i];
            for (int column = 0; column < 4; column++)
            {
                system[(i * 4) + column] *= scale;
            }

            for (int row = 0; row < 3; row++)
            {
                if (row == i)
                {
                    continue;
                }
                float factor = system[(row * 4) + i];
                if (factor == 0.0f)
                {
                    continue;
                }
                for (int column = 0; column < 4; column++)
                {
                    system[(row * 4) + column] -= factor * system[(i * 4) + column];
                }
            }
        }

        delta[0] = system[3];
        delta[1] = system[7];
        delta[2] = system[11];
        return true;
    }

    /// <summary>
    /// Length of a three-element vector
    /// </summary>
    /// <param name="vector">The vector</param>
    /// <returns>Its length</returns>
    private static float Norm(ReadOnlySpan<float> vector)
        => MathF.Sqrt((vector[0] * vector[0]) + (vector[1] * vector[1]) + (vector[2] * vector[2]));
}
