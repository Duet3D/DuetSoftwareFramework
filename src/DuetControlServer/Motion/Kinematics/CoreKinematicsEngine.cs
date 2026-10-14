using System;
using System.Text;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Native;
using DuetControlServer.Motion.Native;

using Code = DuetAPI.Commands.Code;

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
/// move, and so does this.
/// </para>
/// <para>
/// The matrix is indexed by axis and by motor, and a motor is an axis' drive, but what a named
/// geometry describes is letters: CoreXY couples the X and Y motors, whatever positions M584 gave
/// those axes. So each named matrix is written by letter (<see cref="NamedMatrixFor"/>), its letters
/// are the axes the geometry needs, and it is placed once, when the geometry is created, at the
/// indices of the axes that carry them. M584 comes before M669, so M669 selects a named geometry only
/// once those axes exist; an axis M584 creates afterwards is appended and keeps the identity row and
/// column it already has, so the placed matrix never has to be placed again
/// </para>
/// </remarks>
internal sealed class CoreKinematicsEngine : KinematicsEngine
{
    /// <summary>
    /// Size of the matrices. Square over the axis space, so the inverse is defined
    /// </summary>
    private const int MatrixSize = MotionLimits.MaxAxes;

    /// <summary>
    /// A named geometry's inverse matrix, by letter
    /// </summary>
    /// <param name="Letters">The axes the geometry needs, which are also the columns of each row</param>
    /// <param name="Rows">Each row that differs from the identity: the axis it is for, and how much the motor of each of <paramref name="Letters"/> turns per mm of it</param>
    /// <remarks>
    /// Every row not given, and every axis not among the letters, keeps the identity: a motor of its own
    /// </remarks>
    private sealed record NamedMatrix(string Letters, params (char Axis, float[] Motors)[] Rows);

    /// <summary>
    /// The axes this geometry needs: those its name's matrix is written for
    /// </summary>
    private readonly string _requiredLetters;

    /// <summary>Maps axis coordinates to motor positions: inverse[axis, motor]</summary>
    private readonly float[,] _inverse;

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
    /// <remarks>
    /// Which core arrangement was selected, rather than what the matrix now says. M669 can set an
    /// arbitrary matrix on top of a named geometry, and RepRapFirmware keeps reporting the name it
    /// was given rather than trying to recognise the matrix
    /// </remarks>
    public override KinematicsName Kind { get; }

    /// <inheritdoc />
    /// <remarks>
    /// The letters of the named matrix, which stay needed when M669 has given rows of its own: the
    /// geometry is still the one its name says
    /// </remarks>
    protected override string KinematicAxisLetters => _requiredLetters;

    /// <summary>
    /// Whether the forward matrix could be derived. False means the geometry is unusable
    /// </summary>
    public bool IsValid { get; private set; }

    /// <summary>
    /// What a singular matrix is reported as, in RepRapFirmware's words (<c>CoreKinematics::Recalc</c>)
    /// </summary>
    private const string InvalidMatrixError = "Invalid kinematics matrix";

    /// <inheritdoc />
    /// <remarks>
    /// The placed matrix cannot be inverted, so there is no way back from motor positions to axis
    /// positions. RepRapFirmware reports this whenever it recalculates the matrix and carries on with
    /// a zeroed forward matrix; here it is also why every move is refused
    /// </remarks>
    public override string? ConfigurationError => IsValid ? null : InvalidMatrixError;

    /// <summary>
    /// Create a geometry from its inverse matrix
    /// </summary>
    /// <param name="name">Which core arrangement this is</param>
    /// <param name="inverseMatrix">
    /// Rows are axes, columns are motors, both by index. Entries outside the given rows and columns
    /// are taken to be the identity, so a 3x3 matrix describes a machine whose fourth and later axes
    /// each have their own motor
    /// </param>
    /// <remarks>
    /// For a matrix that is already placed, as the object model holds it. A geometry selected by name
    /// is made by <see cref="TryCreate"/> instead, which places its matrix by letter
    /// </remarks>
    public CoreKinematicsEngine(KinematicsName name, float[][] inverseMatrix)
        : this(name, MatrixFrom(inverseMatrix))
    {
    }

    /// <summary>
    /// The matrix a whole inverse matrix describes
    /// </summary>
    /// <param name="inverseMatrix">Rows are axes, columns are motors, both by index</param>
    /// <returns>The matrix, the identity beyond the rows given</returns>
    private static float[,] MatrixFrom(float[][] inverseMatrix)
    {
        float[,] matrix = Identity();
        for (int axis = 0; axis < inverseMatrix.Length && axis < MatrixSize; axis++)
        {
            float[] row = inverseMatrix[axis];

            // A described row replaces the identity entry, so a row of all zeroes really does mean
            // "no motor moves for this axis" rather than silently keeping the diagonal
            for (int motor = 0; motor < MatrixSize; motor++)
            {
                matrix[axis, motor] = motor < row.Length ? row[motor] : 0.0f;
            }
        }
        return matrix;
    }

    /// <summary>
    /// Create a geometry from its placed inverse matrix
    /// </summary>
    /// <param name="name">Which core arrangement this is</param>
    /// <param name="inverse">The inverse matrix by axis and motor index</param>
    private CoreKinematicsEngine(KinematicsName name, float[,] inverse)
    {
        Kind = name;
        _requiredLetters = NamedMatrixFor(name)?.Letters ?? string.Empty;
        _inverse = inverse;
        Recalculate();
    }

    /// <summary>
    /// The well-known geometries, by name
    /// </summary>
    /// <param name="name">Which core arrangement</param>
    /// <param name="axes">The machine's axes, which the named matrix is placed at</param>
    /// <returns>The engine, or null if that geometry is not a core one</returns>
    /// <remarks>
    /// The named matrix is placed here, once. A letter it needs that <paramref name="axes"/> does not
    /// have is left out of the placed matrix, and the geometry refuses to plan
    /// (<see cref="KinematicsEngine.MissingAxesError"/>); the caller refuses it rather than keep it
    /// </remarks>
    public static CoreKinematicsEngine? TryCreate(KinematicsName name, AxisIndices axes)
    {
        NamedMatrix? matrix = NamedMatrixFor(name);
        if (matrix is null)
        {
            return null;
        }

        CoreKinematicsEngine engine = new(name, Place(matrix, axes));
        engine.SetAxes(axes);
        return engine;
    }

    /// <summary>
    /// A core geometry with the matrix its name implies, for before M669 has been seen
    /// </summary>
    /// <param name="name">Which core arrangement</param>
    /// <param name="axes">The machine's axes, which the named matrix is placed at</param>
    /// <returns>The engine, Cartesian if the name is not a core geometry's</returns>
    /// <remarks>
    /// The counterpart of every other geometry's <c>CreateDefault</c>, taking a name because six of
    /// them share this class and only the matrix tells them apart. Unlike <see cref="TryCreate"/> it
    /// always returns a geometry: a machine whose kinematics could not be worked out is an
    /// unconfigured one, and an unconfigured machine is Cartesian
    /// </remarks>
    public static CoreKinematicsEngine CreateDefault(KinematicsName name, AxisIndices axes)
        => TryCreate(name, axes) ?? TryCreate(KinematicsName.Cartesian, axes)!;

    /// <summary>
    /// The inverse matrix a named core geometry implies
    /// </summary>
    /// <param name="name">Which core arrangement</param>
    /// <returns>Its matrix, or null if that geometry is not a core one</returns>
    /// <remarks>
    /// RepRapFirmware's, from <c>CoreKinematics::CoreKinematics</c>, which starts from the identity and
    /// sets <c>inverseMatrix(axis, motor)</c> entries for its first five axes, X, Y, Z, U and V. Each row
    /// here is one of those axes by letter, and its values are the motors of <see cref="NamedMatrix.Letters"/>
    /// in that order; RepRapFirmware's rows that are the identity are left out. Selecting a geometry by
    /// name is selecting one of these, and M669's axis-letter parameters then replace individual rows
    /// </remarks>
    private static NamedMatrix? NamedMatrixFor(KinematicsName name)
        => name switch
        {
            KinematicsName.Cartesian => new(""),

            KinematicsName.CoreXY => new("XY",
                //    X   Y
                ('X', [1, 1]),
                ('Y', [1, -1])),

            // A 3:1 reduction on Z, as in the original CoreXZ design
            KinematicsName.CoreXZ => new("XZ",
                //    X  Z
                ('X', [1, 1]),
                ('Z', [3, -3])),

            // CoreXY with a U axis driven by the U and V motors, which Y drives as well. V cannot be
            // commanded on its own but keeps its identity row, so that the matrix can be inverted
            KinematicsName.CoreXYU => new("XYUV",
                //    X  Y   U  V
                ('X', [1, 1, 0, 0]),
                ('Y', [1, -1, 1, -1]),
                ('U', [0, 0, 1, 1])),

            // Two CoreXY pairs, X and Y on one and U and V on the other
            KinematicsName.CoreXYUV => new("XYUV",
                //    X  Y   U  V
                ('X', [1, 1, 0, 0]),
                ('Y', [1, -1, 0, 0]),
                ('U', [0, 0, 1, 1]),
                ('V', [0, 0, 1, -1])),

            KinematicsName.MarkForged => new("XY",
                //    X   Y
                ('Y', [-1, 1])),

            _ => null
        };

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Ported from <c>CoreKinematics::Configure</c>, which only M669 reaches. Each axis letter carries
    /// that axis' row of the inverse matrix, which is how a machine that is none of the named
    /// arrangements is described. The row goes to the axis carrying the letter, wherever it is, and its
    /// values are the motors of the machine's axes in axis order, so it can be no longer than the
    /// machine has axes. A row shorter than that leaves the rest of that row as it was, so a parameter
    /// can correct the motors it mentions without restating the ones it does not.
    /// </para>
    /// <para>
    /// A row for a letter the machine has no axis for is refused, where RepRapFirmware only looks for
    /// the letters of axes that exist and ignores the rest: M584 comes before M669, so the axis the row
    /// is for has to exist already. A matrix that cannot be inverted is refused with RepRapFirmware's
    /// message, and in both cases the geometry that was there stays
    /// </para>
    /// </remarks>
    /// <exception cref="GCodeException">A row is for an axis the machine does not have, or the matrix cannot be inverted</exception>
    public override KinematicsEngine Configure(Code code, ref bool seen)
    {
        if (code.MajorNumber != 669)
        {
            return this;
        }

        AxisIndices axes = Axes;
        StringBuilder missing = new();
        bool anyRow = false;
        foreach (char letter in Axis.Letters)
        {
            if (code.HasParameter(letter))
            {
                anyRow = true;
                if (axes.IndexOf(letter) < 0)
                {
                    missing.Append(letter);
                }
            }
        }

        if (!anyRow)
        {
            return this;
        }
        if (missing.Length > 0)
        {
            throw new GCodeException(AxisIndices.NoAxisMessage(missing.ToString()));
        }

        // The rows are written into the matrix as it is placed now, which is final: every letter the
        // named matrix or a row names is an axis that exists, and axes are never moved or removed
        float[,] inverse = (float[,])_inverse.Clone();
        foreach (char letter in Axis.Letters)
        {
            if (code.TryGetFloatArray(letter, axes.Count, out float[]? values))
            {
                int axis = axes.IndexOf(letter);
                for (int motor = 0; motor < values.Length; motor++)
                {
                    inverse[axis, motor] = values[motor];
                }
            }
        }
        seen = true;

        // A matrix that cannot be inverted describes a machine whose motors cannot reach every
        // position, so the geometry that was there is kept rather than replaced with one that cannot plan
        CoreKinematicsEngine engine = new(Kind, inverse);
        engine.SetAxes(axes);
        if (!engine.IsValid)
        {
            throw new GCodeException(InvalidMatrixError);
        }
        return engine;
    }

    /// <inheritdoc />
    public override void WriteTo(DuetAPI.ObjectModel.Kinematics kinematics)
    {
        base.WriteTo(kinematics);

        CoreKinematics core = (CoreKinematics)kinematics;
        core.InverseMatrix.Clear();
        core.ForwardMatrix.Clear();
        for (int axis = 0; axis < MatrixSize; axis++)
        {
            float[] inverse = new float[MatrixSize];
            float[] forward = new float[MatrixSize];
            for (int i = 0; i < MatrixSize; i++)
            {
                inverse[i] = _inverse[axis, i];
                forward[i] = _forward[axis, i];
            }
            core.InverseMatrix.Add(inverse);
            core.ForwardMatrix.Add(forward);
        }
    }

    /// <summary>
    /// The identity matrix, which is every axis on a motor of its own
    /// </summary>
    /// <returns>A new matrix</returns>
    private static float[,] Identity()
    {
        float[,] matrix = new float[MatrixSize, MatrixSize];
        for (int i = 0; i < MatrixSize; i++)
        {
            matrix[i, i] = 1.0f;
        }
        return matrix;
    }

    /// <summary>
    /// Place a named matrix at the indices of the given axes
    /// </summary>
    /// <param name="named">The named matrix</param>
    /// <param name="axes">The machine's axes</param>
    /// <returns>The inverse matrix by axis and motor index, the identity wherever the named matrix says nothing</returns>
    /// <remarks>
    /// A named row or column whose letter the machine does not have is left out. Such a geometry
    /// refuses to plan (<see cref="KinematicsEngine.MissingAxesError"/>), so this only has to leave the
    /// axes that do exist with a matrix that can be inverted
    /// </remarks>
    private static float[,] Place(NamedMatrix named, AxisIndices axes)
    {
        float[,] inverse = Identity();
        foreach ((char letter, float[] motors) in named.Rows)
        {
            int axis = axes.IndexOf(letter);
            if (axis < 0 || axis >= MatrixSize)
            {
                continue;
            }

            // The named row replaces the whole row, so a motor it does not mention does not move
            for (int motor = 0; motor < MatrixSize; motor++)
            {
                inverse[axis, motor] = 0.0f;
            }
            for (int column = 0; column < motors.Length; column++)
            {
                int motor = axes.IndexOf(named.Letters[column]);
                if (motor >= 0 && motor < MatrixSize)
                {
                    inverse[axis, motor] = motors[column];
                }
            }
        }

        return inverse;
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
        // A linear combination has exactly one solution, so there is no pose to choose and
        // isCoordinated makes no difference here. A singular one has no way back from the motor
        // positions, and one missing an axis its name needs is not the machine it says it is, so
        // nothing is planned through either
        if (!IsValid || !Kinematic.IsComplete)
        {
            return NativeMovementError.UnreachablePosition;
        }

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
        // are ignored when working out where the machine is. A singular matrix has no forward
        // transform, so the positions the caller holds are left as they are rather than zeroed
        if (!IsValid)
        {
            return;
        }

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
        in PlannedMove move,
        ReadOnlySpan<float> maxFeedrates,
        ReadOnlySpan<float> accelerations)
    {
        // Deliberately not the base implementation: on a Cartesian machine X and Y really are limited
        // independently, so a diagonal move may go faster than either axis alone
        ReadOnlySpan<float> normalisedDirectionVector = move.NormalisedDirectionVector;

        // How much of the move each shared motor contributes. A motor that only one axis drives has
        // already been limited by the per-axis pass, so only the shared ones are of interest here
        Span<float> motorMovements = stackalloc float[MatrixSize];
        motorMovements.Clear();

        int axisLimit = Math.Min(move.NumVisibleAxes, Math.Min(MatrixSize, normalisedDirectionVector.Length));
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
    /// Derive the forward matrix and the index bounds from the inverse matrix
    /// </summary>
    /// <remarks>RepRapFirmware's <c>CoreKinematics::Recalc</c></remarks>
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
