using System;
using DuetControlServer.Link.Native;
using DuetControlServer.Motion.Native;

namespace DuetControlServer.Motion.Kinematics;

/// <summary>
/// The matrix-driven geometries: Cartesian, CoreXY, CoreXZ, CoreXYU, CoreXYUV and MarkForged
/// </summary>
/// <remarks>
/// <para>
/// Ported from RepRapFirmware's <c>CoreKinematics</c>. All of these are the same machine as far as
/// the maths is concerned: motor position is a fixed linear combination of axis positions. The
/// inverse matrix holds those combinations - <c>inverse[axis, motor]</c> is how much that motor turns
/// per mm of that axis - and it is the only thing that distinguishes a Cartesian machine from a
/// CoreXY one.
/// </para>
/// <para>
/// The forward matrix, which converts back, is the matrix inverse of that. RepRapFirmware computes it
/// by Gauss-Jordan elimination once when the geometry is configured rather than solving on every
/// move, and so does this
/// </para>
/// </remarks>
internal sealed class CoreKinematicsEngine : KinematicsEngine
{
    /// <summary>
    /// Size of the matrices. Square over the axis space, so the inverse is defined
    /// </summary>
    private const int MatrixSize = MotionLimits.MaxAxes;

    /// <summary>Maps axis coordinates to motor positions: inverse[axis, motor]</summary>
    private readonly float[,] _inverse = new float[MatrixSize, MatrixSize];

    /// <summary>Maps motor positions back to axis coordinates: forward[motor, axis]</summary>
    private readonly float[,] _forward = new float[MatrixSize, MatrixSize];

    /// <summary>Which drives control each axis</summary>
    private readonly uint[] _controllingDrives = new uint[MatrixSize];

    /// <summary>First and last axis each motor is affected by, to keep the inner loops short</summary>
    private readonly int[] _firstAxis = new int[MatrixSize];
    private readonly int[] _lastAxis = new int[MatrixSize];

    /// <summary>First and last motor each axis is affected by</summary>
    private readonly int[] _firstMotor = new int[MatrixSize];
    private readonly int[] _lastMotor = new int[MatrixSize];

    /// <summary>True if the axis is moved by a motor that another axis also moves</summary>
    private readonly bool[] _hasSharedMotor = new bool[MatrixSize];

    /// <inheritdoc />
    public override string Name { get; }

    /// <summary>
    /// Whether the forward matrix could be derived. False means the geometry is unusable
    /// </summary>
    public bool IsValid { get; private set; }

    /// <summary>
    /// Create a geometry from its inverse matrix
    /// </summary>
    /// <param name="name">Name of the geometry</param>
    /// <param name="inverseMatrix">
    /// Rows are axes, columns are motors. Entries outside the given rows and columns are taken to be
    /// the identity, so a 3x3 matrix describes a machine whose fourth and later axes each have their
    /// own motor
    /// </param>
    public CoreKinematicsEngine(string name, float[][] inverseMatrix)
    {
        Name = name;

        // Start from the identity so that axes the caller did not describe keep a motor of their own
        for (int i = 0; i < MatrixSize; i++)
        {
            _inverse[i, i] = 1.0f;
        }

        for (int axis = 0; axis < inverseMatrix.Length && axis < MatrixSize; axis++)
        {
            float[] row = inverseMatrix[axis];

            // A described row replaces the identity entry, so a row of all zeroes really does mean
            // "no motor moves for this axis" rather than silently keeping the diagonal
            for (int motor = 0; motor < MatrixSize; motor++)
            {
                _inverse[axis, motor] = motor < row.Length ? row[motor] : 0.0f;
            }
        }

        Recalculate();
    }

    /// <summary>
    /// The well-known geometries, by the name M669 uses
    /// </summary>
    /// <param name="name">Geometry name, case-insensitive</param>
    /// <returns>The engine, or null if the name is not a core geometry</returns>
    /// <remarks>
    /// The matrices are RepRapFirmware's, from <c>CoreKinematics::CoreKinematics</c>. Each row says
    /// how much each motor turns per mm of that axis
    /// </remarks>
    public static CoreKinematicsEngine? TryCreate(string name)
    {
        float[][]? matrix = name.ToLowerInvariant() switch
        {
            "cartesian" => [[1, 0, 0], [0, 1, 0], [0, 0, 1]],
            "corexy" => [[1, 1, 0], [1, -1, 0], [0, 0, 1]],
            "corexz" => [[1, 0, 1], [0, 1, 0], [1, 0, -1]],
            "corexyu" => [[1, 1, 0, 0], [1, -1, 0, 0], [0, 0, 1, 0], [1, -1, 0, -2]],
            "corexyuv" =>
            [
                [1, 1, 0, 0, 0],
                [1, -1, 0, 0, 0],
                [0, 0, 1, 0, 0],
                [1, -1, 0, -2, 0],
                [1, 1, 0, 0, -2]
            ],
            "markforged" => [[1, 0, 0], [-1, 1, 0], [0, 0, 1]],
            _ => null
        };

        return matrix is null ? null : new CoreKinematicsEngine(name, matrix);
    }

    /// <inheritdoc />
    public override NativeMovementError CartesianToMotorSteps(
        ReadOnlySpan<float> machinePos,
        ReadOnlySpan<float> stepsPerMm,
        int numVisibleAxes,
        int numTotalAxes,
        Span<int> motorPos)
    {
        NativeMovementError result = NativeMovementError.Ok;
        int motorLimit = Math.Min(numTotalAxes, Math.Min(MatrixSize, Math.Min(motorPos.Length, stepsPerMm.Length)));

        for (int motor = 0; motor < motorLimit; motor++)
        {
            int axisLimit = Math.Min(numVisibleAxes, _lastAxis[motor] + 1);
            axisLimit = Math.Min(axisLimit, machinePos.Length);

            int axis = _firstAxis[motor];
            if (axis >= axisLimit)
            {
                // No visible axis drives this motor, so it stays where the caller left it
                continue;
            }

            float movement = 0.0f;
            for (; axis < axisLimit; axis++)
            {
                movement += _inverse[axis, motor] * machinePos[axis];
            }

            if (TryRoundToInt32(movement * stepsPerMm[motor], out int steps))
            {
                motorPos[motor] = steps;
            }
            else
            {
                result = NativeMovementError.MicrostepPositionTooLarge;
            }
        }

        return result;
    }

    /// <inheritdoc />
    public override void MotorStepsToCartesian(
        ReadOnlySpan<int> motorPos,
        ReadOnlySpan<float> stepsPerMm,
        int numVisibleAxes,
        int numTotalAxes,
        Span<float> machinePos)
    {
        // Where there are more motors than visible axes - CoreXYU has a V motor - the trailing ones
        // are ignored when working out where the machine is
        int axisLimit = Math.Min(numVisibleAxes, Math.Min(MatrixSize, machinePos.Length));

        for (int axis = 0; axis < axisLimit; axis++)
        {
            float position = 0.0f;
            int motorLimit = Math.Min(numVisibleAxes, _lastMotor[axis] + 1);
            motorLimit = Math.Min(motorLimit, Math.Min(motorPos.Length, stepsPerMm.Length));

            for (int motor = _firstMotor[axis]; motor < motorLimit; motor++)
            {
                float factor = _forward[motor, axis];
                if (factor != 0.0f)
                {
                    position += factor * motorPos[motor] / stepsPerMm[motor];
                }
            }
            machinePos[axis] = position;
        }
    }

    /// <inheritdoc />
    public override void LimitSpeedAndAcceleration(
        ref MoveLimits limits,
        ReadOnlySpan<float> normalisedDirectionVector,
        int numVisibleAxes,
        ReadOnlySpan<float> maxFeedrates,
        ReadOnlySpan<float> accelerations)
    {
        // How much of the move each shared motor contributes. A motor that only one axis drives has
        // already been limited by the per-axis pass, so only the shared ones are of interest here
        Span<float> motorMovements = stackalloc float[MatrixSize];
        motorMovements.Clear();

        int axisLimit = Math.Min(numVisibleAxes, Math.Min(MatrixSize, normalisedDirectionVector.Length));
        for (int axis = 0; axis < axisLimit; axis++)
        {
            if (!_hasSharedMotor[axis])
            {
                continue;
            }

            float dv = normalisedDirectionVector[axis];
            if (dv == 0.0f)
            {
                continue;
            }

            for (int motor = 0; motor < MatrixSize; motor++)
            {
                float factor = _inverse[axis, motor];
                if (factor != 0.0f)
                {
                    motorMovements[motor] += factor * dv;
                }
            }
        }

        for (int motor = 0; motor < MatrixSize; motor++)
        {
            float movement = MathF.Abs(motorMovements[motor]);
            if (movement != 0.0f && motor < maxFeedrates.Length && motor < accelerations.Length)
            {
                // The motor turns `movement` times as fast as the move itself, so whatever it can do
                // divided by that is what the move can do
                limits.Limit(maxFeedrates[motor] / movement, accelerations[motor] / movement);
            }
        }
    }

    /// <inheritdoc />
    public override uint GetControllingDrives(int axis)
        => (axis >= 0 && axis < MatrixSize) ? _controllingDrives[axis] : base.GetControllingDrives(axis);

    /// <summary>
    /// Whether the given axis shares a motor with another axis
    /// </summary>
    /// <param name="axis">Axis number</param>
    /// <returns>True if it has no motor to itself</returns>
    public bool HasSharedMotor(int axis) => axis >= 0 && axis < MatrixSize && _hasSharedMotor[axis];

    /// <summary>
    /// Derive the forward matrix and the index bounds from the inverse matrix
    /// </summary>
    private void Recalculate()
    {
        IsValid = TryInvert(_inverse, _forward);
        if (!IsValid)
        {
            Array.Clear(_forward);
        }

        for (int i = 0; i < MatrixSize; i++)
        {
            _firstMotor[i] = _firstAxis[i] = MatrixSize;
            _lastMotor[i] = _lastAxis[i] = 0;
            _controllingDrives[i] = 0;
        }

        for (int axis = 0; axis < MatrixSize; axis++)
        {
            for (int motor = 0; motor < MatrixSize; motor++)
            {
                if (_inverse[axis, motor] != 0.0f)
                {
                    // This axis needs this motor driven
                    _firstAxis[motor] = Math.Min(_firstAxis[motor], axis);
                    _lastAxis[motor] = Math.Max(_lastAxis[motor], axis);
                    _controllingDrives[axis] |= 1u << motor;
                }

                if (_forward[motor, axis] != 0.0f)
                {
                    // This motor affects this axis
                    _firstMotor[axis] = Math.Min(_firstMotor[axis], motor);
                    _lastMotor[axis] = Math.Max(_lastMotor[axis], motor);
                    _controllingDrives[axis] |= 1u << motor;
                }
            }
        }

        // An axis has a shared motor if any motor it drives is also driven by another axis
        for (int axis = 0; axis < MatrixSize; axis++)
        {
            bool shared = false;
            for (int motor = 0; motor < MatrixSize && !shared; motor++)
            {
                if (_inverse[axis, motor] == 0.0f)
                {
                    continue;
                }
                for (int otherAxis = 0; otherAxis < MatrixSize; otherAxis++)
                {
                    if (otherAxis != axis && _inverse[otherAxis, motor] != 0.0f)
                    {
                        shared = true;
                        break;
                    }
                }
            }
            _hasSharedMotor[axis] = shared;
        }
    }

    /// <summary>
    /// Invert a square matrix by Gauss-Jordan elimination
    /// </summary>
    /// <param name="source">Matrix to invert</param>
    /// <param name="destination">Filled in with the inverse</param>
    /// <returns>False if the matrix is singular, i.e. the geometry does not describe a real machine</returns>
    private static bool TryInvert(float[,] source, float[,] destination)
    {
        // The source in the left half and the identity in the right; reducing the left to the
        // identity turns the right into the inverse
        float[,] work = new float[MatrixSize, 2 * MatrixSize];
        for (int i = 0; i < MatrixSize; i++)
        {
            for (int j = 0; j < MatrixSize; j++)
            {
                work[i, j] = source[i, j];
            }
            work[i, i + MatrixSize] = 1.0f;
        }

        for (int i = 0; i < MatrixSize; i++)
        {
            // Partial pivoting: swap in the row with the largest leading value, both for numerical
            // stability and to move a zero off the diagonal
            int pivot = i;
            for (int j = i + 1; j < MatrixSize; j++)
            {
                if (MathF.Abs(work[j, i]) > MathF.Abs(work[pivot, i]))
                {
                    pivot = j;
                }
            }

            if (MathF.Abs(work[pivot, i]) < 1.0e-9f)
            {
                return false;               // singular: no set of motor positions reaches some axis position
            }

            if (pivot != i)
            {
                for (int j = 0; j < 2 * MatrixSize; j++)
                {
                    (work[i, j], work[pivot, j]) = (work[pivot, j], work[i, j]);
                }
            }

            float scale = 1.0f / work[i, i];
            for (int j = 0; j < 2 * MatrixSize; j++)
            {
                work[i, j] *= scale;
            }

            for (int j = 0; j < MatrixSize; j++)
            {
                if (j == i)
                {
                    continue;
                }
                float factor = work[j, i];
                if (factor == 0.0f)
                {
                    continue;
                }
                for (int k = 0; k < 2 * MatrixSize; k++)
                {
                    work[j, k] -= factor * work[i, k];
                }
            }
        }

        for (int i = 0; i < MatrixSize; i++)
        {
            for (int j = 0; j < MatrixSize; j++)
            {
                destination[i, j] = work[i, j + MatrixSize];
            }
        }
        return true;
    }
}
