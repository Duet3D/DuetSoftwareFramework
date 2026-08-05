using System;
using DuetControlServer.Link.Native;
using DuetControlServer.Motion.Kinematics;
using DuetControlServer.Motion.Native;

namespace DuetControlServer.Motion;

/// <summary>
/// What came of trying to build a move
/// </summary>
/// <param name="Error">Ok, NoMovement, or why it could not be built</param>
/// <param name="Length">Bytes written to the destination buffer, 0 unless Error is Ok</param>
internal readonly record struct MoveBuildResult(NativeMovementError Error, int Length)
{
    /// <summary>Whether a submission was produced</summary>
    public bool HasMove => Error == NativeMovementError.Ok && Length > 0;
}

/// <summary>
/// Turns a <see cref="RawMove"/> into the submission the native motion engine takes
/// </summary>
/// <remarks>
/// <para>
/// This is steps 1 to 6 of RepRapFirmware's <c>DDA::InitStandardMove</c>, which is exactly the part
/// that depends on the move alone: where each drive ends up, which way the move points, how long it
/// is, and how fast and how hard it may be pushed. Step 7 onwards - lookahead, melding one move into
/// the next, settling the actual start and end speeds - needs the whole ring of queued moves, and
/// the ring is native. See <c>Motion/MoveParams.h</c> for the seam.
/// </para>
/// <para>
/// The builder is stateful because moves are relative to each other. It keeps where the last move
/// left the machine in both coordinate systems: <see cref="StartCoordinates"/> in axis space, which
/// is what the next move's deltas are measured from, and the motor endpoints, which the native
/// planner differences to get the steps each drive must take. Both have to be corrected when a move
/// stops short - see <see cref="ResyncEndpoints"/>
/// </para>
/// </remarks>
internal sealed class MoveBuilder(MotionParameters parameters)
{
    private const int NumDrives = MotionLimits.MaxAxesPlusExtruders;

    /// <summary>Where the last move left the machine in axis space, mm</summary>
    private readonly float[] _startCoordinates = new float[NumDrives];

    /// <summary>Where the last move left the motors, in microsteps</summary>
    private readonly int[] _endPoints = new int[NumDrives];

    /// <summary>Scratch buffers, so building a move does not allocate</summary>
    private readonly float[] _directionVector = new float[NumDrives];
    private readonly float[] _normalisedDirection = new float[NumDrives];
    private readonly int[] _newEndPoints = new int[NumDrives];
    private readonly float[] _accelerations = new float[NumDrives];

    /// <summary>The machine being planned for, as derived from the object model</summary>
    public MotionParameters Parameters { get; private set; } = parameters;

    /// <summary>Where the last move left the machine in axis space, mm</summary>
    public ReadOnlySpan<float> StartCoordinates => _startCoordinates;

    /// <summary>Where the last move left the motors, in microsteps</summary>
    public ReadOnlySpan<int> EndPoints => _endPoints;

    /// <summary>
    /// Adopt a new machine configuration
    /// </summary>
    /// <param name="newParameters">The configuration</param>
    /// <remarks>
    /// Only safe while nothing is in flight. Steps per mm changing under a queued move would make the
    /// endpoints it was planned against mean something different from what the drives will do
    /// </remarks>
    public void Reconfigure(MotionParameters newParameters) => Parameters = newParameters;

    /// <summary>
    /// Force the machine position, after homing or a move that was cut short
    /// </summary>
    /// <param name="endPoints">Motor positions in microsteps</param>
    /// <remarks>
    /// The axis coordinates are re-derived from the motor positions rather than taken separately, so
    /// the two cannot disagree about where the machine is
    /// </remarks>
    public void ResyncEndpoints(ReadOnlySpan<int> endPoints)
    {
        int count = Math.Min(endPoints.Length, NumDrives);
        endPoints[..count].CopyTo(_endPoints);

        Parameters.Geometry.MotorStepsToCartesian(
            _endPoints, Parameters.StepsPerMm, Parameters.NumAxes, Parameters.NumAxes, _startCoordinates);
    }

    /// <summary>
    /// Re-derive the motor endpoints from the axis coordinates
    /// </summary>
    /// <remarks>
    /// For when steps per mm or microstepping changed underneath: the endpoints are in microsteps, so
    /// the same count means a different position once the conversion changes. Recomputing them from
    /// the axis coordinates keeps the machine where it was in mm, which is what the user asked for by
    /// changing a calibration constant rather than commanding a move. RepRapFirmware does the same
    /// thing by scaling each endpoint by the ratio the steps per mm changed by
    /// </remarks>
    public void RecalculateEndPoints()
        => Parameters.Geometry.CartesianToMotorSteps(
            _startCoordinates, Parameters.StepsPerMm, Parameters.NumAxes, Parameters.NumAxes, _endPoints);

    /// <summary>
    /// Set the axis coordinates without moving anything (G92)
    /// </summary>
    /// <param name="axis">Axis to redefine</param>
    /// <param name="position">Its new position in mm</param>
    public void SetAxisPosition(int axis, float position)
    {
        if (axis < 0 || axis >= Parameters.NumAxes)
        {
            return;
        }

        _startCoordinates[axis] = position;
        Parameters.Geometry.CartesianToMotorSteps(
            _startCoordinates, Parameters.StepsPerMm, Parameters.NumAxes, Parameters.NumAxes, _endPoints);
    }

    /// <summary>
    /// Build a move submission
    /// </summary>
    /// <param name="move">The move to build</param>
    /// <param name="destination">Buffer of at least <see cref="MoveParams.Length"/> bytes</param>
    /// <returns>What came of it</returns>
    /// <remarks>
    /// On success the builder's idea of where the machine is has advanced to the end of this move, so
    /// a submission that is built must also be submitted. NoMovement still advances the axis
    /// coordinates - the user asked to go somewhere that rounds to no steps, and the next move should
    /// be measured from there rather than from where the machine happens to have stopped
    /// </remarks>
    public MoveBuildResult Build(RawMove move, Span<byte> destination)
    {
        float[] stepsPerMm = Parameters.StepsPerMm;
        int numAxes = Parameters.NumAxes;
        int firstExtruderDrive = Parameters.FirstExtruderDrive;

        // --- 1. Compute the new endpoints and the movement vector ---------------------------------

        Array.Clear(_directionVector);
        _endPoints.CopyTo(_newEndPoints, 0);

        bool linearAxesMoving = false, rotationalAxesMoving = false;
        bool xyMoving = false;

        // Whether the coordinates are axis positions to be put through the kinematics, or motor
        // positions to be taken as they are. RepRapFirmware's doMotorMapping, and the distinction is
        // the geometry's rather than the move's: G1 H1 on a CoreXY homes an axis through the
        // kinematics, while the same code on a delta addresses one tower's motor
        bool doMotorMapping = !Parameters.Geometry.IsRawMotorMove(move.MoveType);

        if (doMotorMapping)
        {
            NativeMovementError error = Parameters.Geometry.CartesianToMotorSteps(
                move.Coords, stepsPerMm, numAxes, numAxes, _newEndPoints, move.IsCoordinated);
            if (error != NativeMovementError.Ok)
            {
                // Throw the move away rather than moving somewhere else: the endpoints are what the
                // native planner differences, so a wrong one is a wrong distance, not a rounding error
                return new MoveBuildResult(error, 0);
            }

            for (int axis = 0; axis < numAxes; axis++)
            {
                if ((move.OwnedDrives & (1u << axis)) == 0)
                {
                    // Not ours to move, so it stays where the previous move left it
                    _directionVector[axis] = 0.0f;
                    _newEndPoints[axis] = _endPoints[axis];
                    continue;
                }

                float delta = move.Coords[axis] - _startCoordinates[axis];
                _startCoordinates[axis] = move.Coords[axis];
                _directionVector[axis] = delta;

                if (delta == 0.0f)
                {
                    continue;
                }

                if ((Parameters.RotationalAxes & (1u << axis)) != 0)
                {
                    if (move.RotationalAxesMentioned)
                    {
                        rotationalAxesMoving = true;
                    }
                }
                else if (move.LinearAxesMentioned)
                {
                    linearAxesMoving = true;
                    if (((move.XAxes | move.YAxes) & (1u << axis)) != 0)
                    {
                        // XY movement in user space, before the tool mapping was applied. This is
                        // what decides whether the printing jerk limits apply
                        xyMoving = true;
                    }
                }
            }
        }
        else
        {
            // A raw motor move: the coordinates are motor positions, not axis positions
            for (int axis = 0; axis < numAxes; axis++)
            {
                if ((move.OwnedDrives & (1u << axis)) == 0)
                {
                    _directionVector[axis] = 0.0f;
                    _newEndPoints[axis] = _endPoints[axis];
                    continue;
                }

                float steps = move.Coords[axis] * stepsPerMm[axis];
                if (steps <= -2147483000.0f || steps >= 2147483000.0f)
                {
                    return new MoveBuildResult(NativeMovementError.MicrostepPositionTooLarge, 0);
                }

                _newEndPoints[axis] = (int)MathF.Round(steps);
                int delta = _newEndPoints[axis] - _endPoints[axis];
                _directionVector[axis] = delta / stepsPerMm[axis];

                if (delta != 0)
                {
                    if ((Parameters.RotationalAxes & (1u << axis)) != 0)
                    {
                        rotationalAxesMoving = true;
                    }
                    else
                    {
                        linearAxesMoving = true;
                    }
                }
            }
        }

        // Drives between the axes and the extruders belong to neither and must not move
        for (int drive = numAxes; drive < firstExtruderDrive; drive++)
        {
            _directionVector[drive] = 0.0f;
            _newEndPoints[drive] = _endPoints[drive];
        }

        // --- Extruders -----------------------------------------------------------------------------

        // Probing and stall-homing moves use the reduced limits, which is what M201.1 configures
        float[] configuredAccelerations = move.ReduceAcceleration ? Parameters.ReducedAccelerations : Parameters.Accelerations;
        configuredAccelerations.CopyTo(_accelerations, 0);

        bool extrudersMoving = false, hasForwardExtrusion = false;
        float totalExtrusion = 0.0f;

        for (int drive = firstExtruderDrive; drive < NumDrives; drive++)
        {
            if ((move.OwnedDrives & (1u << drive)) == 0)
            {
                _directionVector[drive] = 0.0f;
                continue;
            }

            // The steps are deliberately not computed here. Extrusion is relative and the native side
            // carries the fraction of a step between moves, so an endpoint would lose it
            float movement = move.Coords[drive];
            _directionVector[drive] = movement;
            if (movement == 0.0f)
            {
                continue;
            }

            totalExtrusion += MathF.Abs(movement);
            extrudersMoving = true;
            if (movement > 0.0f)
            {
                hasForwardExtrusion = true;
            }

            if (xyMoving && move.UsePressureAdvance)
            {
                float compensationClocks = Parameters.PressureAdvanceClocks[drive];
                float jerk = Parameters.InstantDvs[drive];
                if (compensationClocks > 0.0f && jerk > 0.0f)
                {
                    // Pressure advance adds an instant velocity change of acceleration times k, so
                    // the acceleration has to be capped for that change to stay within jerk
                    _accelerations[drive] = MathF.Min(_accelerations[drive], jerk / compensationClocks);
                }
            }
        }

        // --- 2. Throw it away if nothing really moves ------------------------------------------------

        if (!linearAxesMoving && !rotationalAxesMoving && !extrudersMoving)
        {
            // The axis coordinates still advance. The user asked to go somewhere that rounds to no
            // steps; the next move should be measured from there, or the rounding accumulates
            if (doMotorMapping)
            {
                for (int axis = 0; axis < numAxes; axis++)
                {
                    _startCoordinates[axis] = move.Coords[axis];
                }
            }
            return new MoveBuildResult(NativeMovementError.NoMovement, 0);
        }

        // --- 3. Work out the flags --------------------------------------------------------------------

        bool isPrintingMove = xyMoving && hasForwardExtrusion;     // forward, so wipe-while-retracting does not count

        uint flags = 0;
        if (move.CanPauseAfter) { flags |= MoveFlags.CanPauseAfter; }
        if (move.CheckEndstops) { flags |= MoveFlags.CheckEndstops; }
        if (move.UsingStandardFeedrate) { flags |= MoveFlags.UsingStandardFeedrate; }
        if (move.UsePressureAdvance) { flags |= MoveFlags.UsePressureAdvance; }
        if (xyMoving) { flags |= MoveFlags.XyMoving; }
        if (isPrintingMove) { flags |= MoveFlags.IsPrintingMove; }
        if (extrudersMoving && !isPrintingMove) { flags |= MoveFlags.IsNonPrintingExtruderMove; }
        if (hasForwardExtrusion) { flags |= MoveFlags.HasForwardExtrusion; }
        if (move.CheckEndstops || move.MoveType != 0) { flags |= MoveFlags.IsolatedMove; }
        if (move.MoveType == 0) { flags |= MoveFlags.ContinuousRotationShortcut; }

        // --- 4. Normalise the direction vector and get the distance ------------------------------------

        float totalDistance;
        if (linearAxesMoving)
        {
            // NIST section 2.1.2.5 rule A: if any linear axis moves, the feed rate is the linear speed
            float tiltX = Parameters.Geometry.GetTiltCorrection(0);
            float tiltY = Parameters.Geometry.GetTiltCorrection(1);
            if ((tiltX != 0.0f || tiltY != 0.0f) && numAxes > 2)
            {
                _directionVector[2] += (_directionVector[0] * tiltX) + (_directionVector[1] * tiltY);
            }

            totalDistance = MoveVector.NormaliseLinearMotion(_directionVector, Parameters.LinearAxes, move.XAxes, move.YAxes);
        }
        else if (rotationalAxesMoving)
        {
            totalDistance = MoveVector.Normalise(_directionVector, Parameters.RotationalAxes);
        }
        else
        {
            // Extruder-only. Normalise so the magnitude is the total absolute movement, which gives
            // the right feed rate for a mixing extruder
            totalDistance = totalExtrusion;
            if (totalDistance > 0.0f)
            {
                MoveVector.Scale(_directionVector, 1.0f / totalDistance);
            }
        }

        if (totalDistance <= 0.0f)
        {
            return new MoveBuildResult(NativeMovementError.NoMovement, 0);
        }

        // --- 5. Maximum acceleration ---------------------------------------------------------------------

        _directionVector.CopyTo(_normalisedDirection, 0);
        MoveVector.Absolute(_normalisedDirection);

        float maxAcceleration = MoveVector.VectorBoxIntersection(_normalisedDirection, _accelerations);

        if (xyMoving)
        {
            // M204: a separate ceiling for printing and travel moves
            float limit = isPrintingMove ? Parameters.MaxPrintingAcceleration : Parameters.MaxTravelAcceleration;
            maxAcceleration = MathF.Min(maxAcceleration, limit);
        }

        // --- 6. Requested speed ---------------------------------------------------------------------------

        float requestedSpeed;
        if (move.InverseTimeMode)
        {
            // G93 names a time for the whole move, so how fast it has to go is only known now that
            // the distance is. This is RepRapFirmware's totalDistance/feedRate, with the duration in
            // step clocks
            float durationClocks = move.DurationSec * MotionLimits.StepClockRate;
            if (durationClocks <= 0.0f)
            {
                return new MoveBuildResult(NativeMovementError.NoMovement, 0);
            }
            requestedSpeed = totalDistance / durationClocks;
        }
        else
        {
            requestedSpeed = move.FeedRateMmPerSec / MotionLimits.StepClockRate;
        }

        if (!doMotorMapping && Parameters.Geometry is LinearDeltaKinematicsEngine)
        {
            // A raw motor move is run through the Cartesian motion system, so the feed rate would be
            // read as a speed through Cartesian space. On a delta that is not what the user meant:
            // homing a tower should run that tower at the requested speed, so scale the feed rate up
            // by the largest component of the unit vector. RepRapFirmware limits this correction to
            // linear deltas, which are the geometry where a homing move is a tower move
            float maxComponent = 0.0f;
            for (int axis = 0; axis < numAxes; axis++)
            {
                maxComponent = MathF.Max(maxComponent, _normalisedDirection[axis]);
            }
            if (maxComponent > 0.0f)
            {
                requestedSpeed /= maxComponent;
            }
        }

        // The minimum comes first and the maximum second, deliberately. A move with a tiny XY
        // component and a lot of extrusion may have to run slower than the configured minimum, and
        // clamping to a range would raise it back up
        requestedSpeed = MathF.Max(requestedSpeed, Parameters.MinFeedrate);
        requestedSpeed = MathF.Min(requestedSpeed, MoveVector.VectorBoxIntersection(_normalisedDirection, Parameters.MaxFeedrates));

        MoveLimits limits = new() { RequestedSpeed = requestedSpeed, MaxAcceleration = maxAcceleration };
        if (doMotorMapping)
        {
            PlannedMove plannedMove = new()
            {
                NormalisedDirectionVector = _normalisedDirection,
                StartMotorPos = _endPoints,
                EndMotorPos = _newEndPoints,
                StepsPerMm = stepsPerMm,
                NumVisibleAxes = numAxes,
                TotalDistance = totalDistance,
                ContinuousRotationShortcut = (flags & MoveFlags.ContinuousRotationShortcut) != 0
            };
            Parameters.Geometry.LimitSpeedAndAcceleration(
                ref limits, plannedMove, Parameters.MaxFeedrates, _accelerations);
        }

        // --- Write the submission -----------------------------------------------------------------------

        MoveParamsHeader header = new()
        {
            MoveId = move.MoveId,
            OwnedDrives = move.OwnedDrives,
            Flags = flags,
            TotalDistance = totalDistance,
            MaxAcceleration = limits.MaxAcceleration,
            RequestedSpeed = limits.RequestedSpeed,
            RingNumber = move.RingNumber,
            NumDrives = NumDrives
        };

        int length = MoveParams.Write(destination, header, _newEndPoints, _directionVector, move.StopOnInput);

        // The machine is now where this move leaves it, so a move that is built must be submitted
        _newEndPoints.CopyTo(_endPoints, 0);

        return new MoveBuildResult(NativeMovementError.Ok, length);
    }
}
