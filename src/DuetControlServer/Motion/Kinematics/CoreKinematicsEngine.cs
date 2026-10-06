using System;
using System.Collections.Generic;
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
/// those axes. So the named matrices and the rows M669 gives by letter are kept by letter and placed
/// at the indices of the axes that carry those letters, again whenever the axes change. A row for an
/// axis that does not exist yet waits until M584 creates it, which is what lets config.g select and
/// describe the geometry before it creates the axes
/// </para>
/// </remarks>
internal sealed class CoreKinematicsEngine : KinematicsEngine
{
    /// <summary>
    /// Size of the matrices. Square over the axis space, so the inverse is defined
    /// </summary>
    private const int MatrixSize = MotionLimits.MaxAxes;

    /// <summary>
    /// The letters the named matrices' rows and columns stand for, in order
    /// </summary>
    /// <remarks>
    /// The axes RepRapFirmware's named matrices are written for, which are its first five axes
    /// </remarks>
    public const string NamedMatrixLetters = "XYZUV";

    /// <summary>
    /// The matrix the named rows and the rows by letter are placed over, by axis and motor index
    /// </summary>
    /// <remarks>
    /// The identity for a geometry selected by name, so that an axis nothing describes keeps a motor
    /// of its own. A geometry built from a whole matrix - one adopted from the object model - has
    /// that matrix here instead, and then nothing is placed over it until M669 gives a row
    /// </remarks>
    private readonly float[,] _base;

    /// <summary>
    /// The named geometry's matrix, rows and columns in <see cref="NamedMatrixLetters"/> order, or
    /// empty for one built from a whole matrix
    /// </summary>
    private readonly float[][] _namedRows;

    /// <summary>
    /// Rows M669 has given, by axis letter, each as many motors long as it gave
    /// </summary>
    private readonly IReadOnlyDictionary<char, float[]> _rowsByLetter;

    /// <summary>
    /// The matrix placed for the current axes, and what follows from it
    /// </summary>
    /// <remarks>
    /// Replaced as a whole when the axes change, so a reader without a lock never sees half of it
    /// </remarks>
    private Matrices _matrices;

    /// <inheritdoc />
    /// <remarks>
    /// Which core arrangement was selected, rather than what the matrix now says. M669 can set an
    /// arbitrary matrix on top of a named geometry, and RepRapFirmware keeps reporting the name it
    /// was given rather than trying to recognise the matrix
    /// </remarks>
    public override KinematicsName Kind { get; }

    /// <summary>
    /// Whether the forward matrix could be derived. False means the geometry is unusable
    /// </summary>
    public bool IsValid => _matrices.IsValid;

    /// <summary>
    /// What a singular matrix is reported as, in RepRapFirmware's words (<c>CoreKinematics::Recalc</c>)
    /// </summary>
    public const string InvalidMatrixError = "Invalid kinematics matrix";

    /// <inheritdoc />
    /// <remarks>
    /// The placed matrix cannot be inverted, so there is no way back from motor positions to axis
    /// positions. RepRapFirmware reports this whenever it recalculates the matrix and carries on with
    /// a zeroed forward matrix; here it is also why every move is refused
    /// </remarks>
    public override string? ConfigurationError => _matrices.IsValid ? null : InvalidMatrixError;

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
    /// is made by <see cref="TryCreate(KinematicsName)"/> instead, which places its matrix by letter
    /// </remarks>
    public CoreKinematicsEngine(KinematicsName name, float[][] inverseMatrix)
        : this(name, BaseFrom(inverseMatrix), [], new Dictionary<char, float[]>())
    {
    }

    /// <summary>
    /// The base matrix a whole inverse matrix describes
    /// </summary>
    /// <param name="inverseMatrix">Rows are axes, columns are motors, both by index</param>
    /// <returns>The matrix, the identity beyond the rows given</returns>
    private static float[,] BaseFrom(float[][] inverseMatrix)
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
    /// Create a geometry from what it is defined by
    /// </summary>
    /// <param name="name">Which core arrangement this is</param>
    /// <param name="baseMatrix">The matrix by index the rest is placed over</param>
    /// <param name="namedRows">The named geometry's matrix by letter</param>
    /// <param name="rowsByLetter">Rows M669 has given</param>
    private CoreKinematicsEngine(KinematicsName name, float[,] baseMatrix, float[][] namedRows,
                                 IReadOnlyDictionary<char, float[]> rowsByLetter)
    {
        Kind = name;
        _base = baseMatrix;
        _namedRows = namedRows;
        _rowsByLetter = rowsByLetter;
        _matrices = Place(Axes);
    }

    /// <summary>
    /// The well-known geometries, by the name M669 uses
    /// </summary>
    /// <param name="name">Geometry name, case-insensitive</param>
    /// <returns>The engine, or null if the name is not a core geometry</returns>
    public static CoreKinematicsEngine? TryCreate(string name) => TryCreate(KinematicsNameConverter.Parse(name));

    /// <summary>
    /// The well-known geometries, by name
    /// </summary>
    /// <param name="name">Which core arrangement</param>
    /// <returns>The engine, or null if that geometry is not a core one</returns>
    /// <remarks>
    /// Its matrix is placed when the geometry learns the machine's axes, see
    /// <see cref="KinematicsEngine.SetAxes"/>; until then every axis has a motor of its own
    /// </remarks>
    public static CoreKinematicsEngine? TryCreate(KinematicsName name)
    {
        float[][]? matrix = DefaultMatrixFor(name);
        return matrix is null ? null : new CoreKinematicsEngine(name, Identity(), matrix, new Dictionary<char, float[]>());
    }

    /// <summary>
    /// A core geometry with the matrix its name implies, for before M669 has been seen
    /// </summary>
    /// <param name="name">Which core arrangement</param>
    /// <returns>The engine, Cartesian if the name is not a core geometry's</returns>
    /// <remarks>
    /// The counterpart of every other geometry's <c>CreateDefault</c>, taking a name because six of
    /// them share this class and only the matrix tells them apart. Unlike <see cref="TryCreate"/> it
    /// always returns a geometry: a machine whose kinematics could not be worked out is an
    /// unconfigured one, and an unconfigured machine is Cartesian
    /// </remarks>
    public static CoreKinematicsEngine CreateDefault(KinematicsName name)
        => TryCreate(name) ?? TryCreate(KinematicsName.Cartesian)!;

    /// <summary>
    /// The inverse matrix a named core geometry implies
    /// </summary>
    /// <param name="name">Which core arrangement</param>
    /// <returns>Its matrix, or null if that geometry is not a core one</returns>
    /// <remarks>
    /// RepRapFirmware's, from <c>CoreKinematics::CoreKinematics</c>. Each row says how much each motor
    /// turns per mm of that axis. The rows and the columns are the axes lettered as in
    /// <see cref="NamedMatrixLetters"/>, in that order, which is where RepRapFirmware's first axes always
    /// are; here they are placed wherever those letters are. Selecting a geometry by name is selecting
    /// one of these, and M669's axis-letter parameters then replace individual rows of it
    /// </remarks>
    public static float[][]? DefaultMatrixFor(KinematicsName name)
        => name switch
        {
            KinematicsName.Cartesian => [[1, 0, 0], [0, 1, 0], [0, 0, 1]],
            KinematicsName.CoreXY => [[1, 1, 0], [1, -1, 0], [0, 0, 1]],
            KinematicsName.CoreXZ => [[1, 0, 1], [0, 1, 0], [1, 0, -1]],
            KinematicsName.CoreXYU => [[1, 1, 0, 0], [1, -1, 0, 0], [0, 0, 1, 0], [1, -1, 0, -2]],
            KinematicsName.CoreXYUV =>
            [
                [1, 1, 0, 0, 0],
                [1, -1, 0, 0, 0],
                [0, 0, 1, 0, 0],
                [1, -1, 0, -2, 0],
                [1, 1, 0, 0, -2]
            ],
            KinematicsName.MarkForged => [[1, 0, 0], [-1, 1, 0], [0, 0, 1]],
            _ => null
        };

    /// <summary>
    /// One row of the inverse matrix: how much each motor turns per mm of that axis
    /// </summary>
    /// <param name="axis">Axis number</param>
    /// <returns>The row, as long as the axis space</returns>
    public float[] GetInverseMatrixRow(int axis)
    {
        float[,] inverse = _matrices.Inverse;
        float[] row = new float[MatrixSize];
        for (int motor = 0; motor < MatrixSize; motor++)
        {
            row[motor] = inverse[axis, motor];
        }
        return row;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Ported from <c>CoreKinematics::Configure</c>. Each axis letter carries that axis' row of the
    /// inverse matrix, which is how a machine that is none of the named arrangements is described.
    /// A row shorter than the axis space leaves the rest of that row as it was, so a parameter can
    /// correct the motors it mentions without restating the ones it does not.
    /// </para>
    /// <para>
    /// The row goes to the axis carrying the letter, wherever it is. RepRapFirmware only looks for the
    /// letters of axes that exist, which on its machines always include X, Y and Z; a row given here
    /// for an axis M584 has not created yet is kept and placed when it is, so the usual config.g order
    /// of M669 before M584 describes the same machine.
    /// </para>
    /// <para>
    /// A matrix that cannot be inverted is refused with RepRapFirmware's message, and the geometry
    /// that was there stays. That is decided here only once every row M669 has given can be placed:
    /// while one still waits for its axis, or names a motor of an axis not created yet, the matrix is
    /// not the machine's final one, so it is accepted and M584 reports it if it turns out singular
    /// when the axes exist (<see cref="KinematicsEngine.ConfigurationError"/>)
    /// </para>
    /// </remarks>
    /// <exception cref="GCodeException">The matrix, placed in full, cannot be inverted</exception>
    public override KinematicsEngine Configure(Code code, ref bool seen)
    {
        Dictionary<char, float[]>? rows = null;
        foreach (char letter in Axis.Letters)
        {
            if (code.TryGetFloatArray(letter, MatrixSize, out float[]? values))
            {
                rows ??= new Dictionary<char, float[]>(_rowsByLetter);
                if (rows.TryGetValue(letter, out float[]? previous) && previous.Length > values.Length)
                {
                    float[] merged = [.. previous];
                    values.CopyTo(merged, 0);
                    values = merged;
                }
                rows[letter] = values;
                seen = true;
            }
        }

        if (rows is null)
        {
            return this;
        }

        // A matrix that cannot be inverted describes a machine whose motors cannot reach every
        // position, so the geometry that was there is kept rather than replaced with one that cannot plan
        CoreKinematicsEngine engine = new(Kind, (float[,])_base.Clone(), _namedRows, rows);
        engine.SetAxes(Axes);
        if (!engine.IsValid && engine.PlacesEveryRow(Axes))
        {
            throw new GCodeException(InvalidMatrixError);
        }
        return engine;
    }

    /// <summary>
    /// Whether every row M669 has given lands in full on the given axes
    /// </summary>
    /// <param name="axes">The machine's axes</param>
    /// <returns>False if a row's letter has no axis yet, or a row turns the motor of an axis not created yet</returns>
    private bool PlacesEveryRow(AxisIndices axes)
    {
        foreach ((char letter, float[] values) in _rowsByLetter)
        {
            if (axes.IndexOf(letter) < 0)
            {
                return false;
            }
            for (int motor = axes.Count; motor < values.Length; motor++)
            {
                if (values[motor] != 0.0f)
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <inheritdoc />
    /// <remarks>The named matrix and the rows by letter go to wherever their letters now are</remarks>
    protected override void OnAxesChanged() => _matrices = Place(Axes);

    /// <inheritdoc />
    public override void WriteTo(DuetAPI.ObjectModel.Kinematics kinematics)
    {
        base.WriteTo(kinematics);

        Matrices matrices = _matrices;
        CoreKinematics core = (CoreKinematics)kinematics;
        core.InverseMatrix.Clear();
        core.ForwardMatrix.Clear();
        for (int axis = 0; axis < MatrixSize; axis++)
        {
            float[] inverse = new float[MatrixSize];
            float[] forward = new float[MatrixSize];
            for (int i = 0; i < MatrixSize; i++)
            {
                inverse[i] = matrices.Inverse[axis, i];
                forward[i] = matrices.Forward[axis, i];
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
    /// Place the matrix this geometry is defined by at the indices of the given axes
    /// </summary>
    /// <param name="axes">The machine's axes</param>
    /// <returns>The placed matrix and what follows from it</returns>
    private Matrices Place(AxisIndices axes)
    {
        float[,] inverse = (float[,])_base.Clone();

        for (int row = 0; row < _namedRows.Length && row < NamedMatrixLetters.Length; row++)
        {
            int axis = axes.IndexOf(NamedMatrixLetters[row]);
            if (axis < 0 || axis >= MatrixSize)
            {
                continue;
            }

            // The named row replaces the whole row, so a motor it does not mention does not move
            for (int motor = 0; motor < MatrixSize; motor++)
            {
                inverse[axis, motor] = 0.0f;
            }
            for (int column = 0; column < _namedRows[row].Length && column < NamedMatrixLetters.Length; column++)
            {
                int motor = axes.IndexOf(NamedMatrixLetters[column]);
                if (motor >= 0 && motor < MatrixSize)
                {
                    inverse[axis, motor] = _namedRows[row][column];
                }
            }
        }

        foreach ((char letter, float[] values) in _rowsByLetter)
        {
            int axis = axes.IndexOf(letter);
            if (axis < 0 || axis >= MatrixSize)
            {
                continue;
            }
            for (int motor = 0; motor < values.Length && motor < MatrixSize; motor++)
            {
                inverse[axis, motor] = values[motor];
            }
        }

        return new Matrices(inverse);
    }

    /// <summary>
    /// A placed inverse matrix and everything derived from it
    /// </summary>
    private sealed class Matrices
    {
        /// <summary>Maps axis coordinates to motor positions: inverse[axis, motor]</summary>
        public readonly float[,] Inverse;

        /// <summary>Maps motor positions back to axis coordinates: forward[motor, axis]</summary>
        public readonly float[,] Forward = new float[MatrixSize, MatrixSize];

        /// <summary>Which drives control each axis</summary>
        public readonly uint[] ControllingDrives = new uint[MatrixSize];

        /// <summary>First and last axis each motor is affected by, to keep the inner loops short</summary>
        public readonly int[] FirstAxis = new int[MatrixSize], LastAxis = new int[MatrixSize];

        /// <summary>First and last motor each axis is affected by</summary>
        public readonly int[] FirstMotor = new int[MatrixSize], LastMotor = new int[MatrixSize];

        /// <summary>True if the axis is moved by a motor that another axis also moves</summary>
        public readonly bool[] HasSharedMotor = new bool[MatrixSize];

        /// <summary>Whether the forward matrix could be derived</summary>
        public readonly bool IsValid;

        /// <summary>
        /// Derive the forward matrix and the index bounds from an inverse matrix
        /// </summary>
        /// <param name="inverse">The inverse matrix, which this takes ownership of</param>
        public Matrices(float[,] inverse)
        {
            Inverse = inverse;
            IsValid = TryInvert(Inverse, Forward);
            if (!IsValid)
            {
                Array.Clear(Forward);
            }

            for (int i = 0; i < MatrixSize; i++)
            {
                FirstMotor[i] = FirstAxis[i] = MatrixSize;
                LastMotor[i] = LastAxis[i] = 0;
            }

            for (int axis = 0; axis < MatrixSize; axis++)
            {
                for (int motor = 0; motor < MatrixSize; motor++)
                {
                    if (Inverse[axis, motor] != 0.0f)
                    {
                        // This axis needs this motor driven
                        FirstAxis[motor] = Math.Min(FirstAxis[motor], axis);
                        LastAxis[motor] = Math.Max(LastAxis[motor], axis);
                        ControllingDrives[axis] |= 1u << motor;
                    }

                    if (Forward[motor, axis] != 0.0f)
                    {
                        // This motor affects this axis
                        FirstMotor[axis] = Math.Min(FirstMotor[axis], motor);
                        LastMotor[axis] = Math.Max(LastMotor[axis], motor);
                        ControllingDrives[axis] |= 1u << motor;
                    }
                }
            }

            // An axis has a shared motor if any motor it drives is also driven by another axis
            for (int axis = 0; axis < MatrixSize; axis++)
            {
                bool shared = false;
                for (int motor = 0; motor < MatrixSize && !shared; motor++)
                {
                    if (Inverse[axis, motor] == 0.0f)
                    {
                        continue;
                    }
                    for (int otherAxis = 0; otherAxis < MatrixSize; otherAxis++)
                    {
                        if (otherAxis != axis && Inverse[otherAxis, motor] != 0.0f)
                        {
                            shared = true;
                            break;
                        }
                    }
                }
                HasSharedMotor[axis] = shared;
            }
        }
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
        // positions, so nothing is planned through it
        Matrices matrices = _matrices;
        if (!matrices.IsValid)
        {
            return NativeMovementError.UnreachablePosition;
        }

        NativeMovementError result = NativeMovementError.Ok;
        int motorLimit = Math.Min(numTotalAxes, Math.Min(MatrixSize, Math.Min(motorPos.Length, stepsPerMm.Length)));

        for (int motor = 0; motor < motorLimit; motor++)
        {
            int axisLimit = Math.Min(numVisibleAxes, matrices.LastAxis[motor] + 1);
            axisLimit = Math.Min(axisLimit, machinePos.Length);

            int axis = matrices.FirstAxis[motor];
            if (axis >= axisLimit)
            {
                // No visible axis drives this motor, so it stays where the caller left it
                continue;
            }

            float movement = 0.0f;
            for (; axis < axisLimit; axis++)
            {
                movement += matrices.Inverse[axis, motor] * machinePos[axis];
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
        Matrices matrices = _matrices;
        if (!matrices.IsValid)
        {
            return;
        }

        int axisLimit = Math.Min(numVisibleAxes, Math.Min(MatrixSize, machinePos.Length));

        for (int axis = 0; axis < axisLimit; axis++)
        {
            float position = 0.0f;
            int motorLimit = Math.Min(numVisibleAxes, matrices.LastMotor[axis] + 1);
            motorLimit = Math.Min(motorLimit, Math.Min(motorPos.Length, stepsPerMm.Length));

            for (int motor = matrices.FirstMotor[axis]; motor < motorLimit; motor++)
            {
                float factor = matrices.Forward[motor, axis];
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
        Matrices matrices = _matrices;

        // How much of the move each shared motor contributes. A motor that only one axis drives has
        // already been limited by the per-axis pass, so only the shared ones are of interest here
        Span<float> motorMovements = stackalloc float[MatrixSize];
        motorMovements.Clear();

        int axisLimit = Math.Min(move.NumVisibleAxes, Math.Min(MatrixSize, normalisedDirectionVector.Length));
        for (int axis = 0; axis < axisLimit; axis++)
        {
            if (!matrices.HasSharedMotor[axis])
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
                float factor = matrices.Inverse[axis, motor];
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
        => (axis >= 0 && axis < MatrixSize) ? _matrices.ControllingDrives[axis] : base.GetControllingDrives(axis);

    /// <summary>
    /// Whether the given axis shares a motor with another axis
    /// </summary>
    /// <param name="axis">Axis number</param>
    /// <returns>True if it has no motor to itself</returns>
    public bool HasSharedMotor(int axis) => axis >= 0 && axis < MatrixSize && _matrices.HasSharedMotor[axis];

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
