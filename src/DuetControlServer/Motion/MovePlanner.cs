using System;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link;
using DuetControlServer.Link.Native;
using DuetControlServer.Motion.Kinematics;
using DuetControlServer.Motion.Native;
using Microsoft.Extensions.Logging;

namespace DuetControlServer.Motion;

/// <summary>
/// How a move submission ended
/// </summary>
internal enum MoveSubmitResult
{
    /// <summary>Queued for execution</summary>
    Queued,

    /// <summary>Nothing to do: the move rounds to no movement</summary>
    NoMovement,

    /// <summary>The engine has no room; the caller should retry</summary>
    Busy,

    /// <summary>The move could not be built</summary>
    Rejected
}

/// <summary>
/// Where a G-code becomes a queued move
/// </summary>
/// <remarks>
/// <para>
/// This owns the three things a move needs and the ordering between them: the machine description,
/// the builder that holds where the last move left the machine, and the user-facing interpreter
/// state. Moves must be built in the order they were commanded, because each one is planned as a
/// delta from the one before, so everything here happens under one lock.
/// </para>
/// <para>
/// There is deliberately no queue of built moves. The native side already has one - a lock-free
/// submission ring sized for a few thousand moves - and adding another here would only mean the same
/// moves waiting in two places, with this side's copy invisible to the diagnostics that report how
/// far ahead the engine is running
/// </para>
/// </remarks>
/// <param name="linkInterface">Link interface</param>
/// <param name="model">Object model, which is where the machine configuration lives</param>
/// <param name="logger">Logger</param>
internal sealed class MovePlanner(
    LinkInterface linkInterface,
    Model.ObjectModel model,
    ILogger<MovePlanner> logger)
{
    /// <summary>
    /// How often to re-check whether the rings have drained
    /// </summary>
    private static readonly TimeSpan StandstillPollInterval = TimeSpan.FromMilliseconds(5);

    private readonly Lock _lock = new();
    private readonly byte[] _buffer = new byte[MoveParams.Length(MotionLimits.MaxAxesPlusExtruders)];
    private readonly int[] _resyncBuffer = new int[MotionLimits.MaxAxesPlusExtruders];

    /// <summary>
    /// Where the builder stood before the move being submitted advanced it, so that a submission the
    /// engine will not take can be undone
    /// </summary>
    private readonly int[] _endPointsBeforeBuild = new int[MotionLimits.MaxAxesPlusExtruders];
    private uint _nextMoveId = 1;
    private readonly uint[] _lastSubmittedMoveId = new uint[MotionLimits.MaxRings];

    /// <summary>
    /// The machine being planned for, as last read from the object model
    /// </summary>
    /// <remarks>
    /// <para>
    /// A derived snapshot, not a second copy of the configuration: the object model is authoritative
    /// and <see cref="ReconfigureAsync"/> is what brings this back into step with it.
    /// </para>
    /// <para>
    /// Held by the builder rather than here, and read through it, because the builder needs it on
    /// every move and two references to it would be two things to keep in step - which is what
    /// <see cref="ReconfigureAsync"/> used to have to do by hand
    /// </para>
    /// </remarks>
    public MotionParameters Parameters => Builder.Parameters;

    /// <summary>
    /// The machine's geometry
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike the rest of <see cref="Parameters"/>, this is not derived from the object model: it is
    /// where M665, M666 and M669 put what they were told, and the object model's
    /// <c>move.kinematics</c> is written from it. See §14 of <c>docs/devel/MCODE_MIGRATION.md</c> for
    /// why this one is the other way round.
    /// </para>
    /// <para>
    /// A machine that has not been configured is Cartesian, which is what RepRapFirmware starts as
    /// </para>
    /// </remarks>
    public KinematicsEngine Geometry { get; private set; } = KinematicsFactory.Create(KinematicsName.Cartesian);

    /// <summary>
    /// Adopt a geometry a configuring M-code has produced
    /// </summary>
    /// <param name="geometry">The new geometry</param>
    /// <remarks>
    /// The caller reconfigures afterwards, which is what puts the geometry into
    /// <see cref="Parameters"/> and pushes the description that follows from it down to the engine.
    /// Only safe at standstill, for the same reason <see cref="ReconfigureAsync"/> is
    /// </remarks>
    public void SetGeometry(KinematicsEngine geometry)
    {
        using (_lock.EnterScope())
        {
            Geometry = geometry;
        }
    }

    /// <summary>Where the last move left the machine, and the state that carries between moves</summary>
    public MoveBuilder Builder { get; }  = new(MotionParameters.CreateDefault());

    /// <summary>
    /// Where the interpreter thinks the machine is going, in user coordinates
    /// </summary>
    /// <remarks>
    /// One per motion system once M596 is ported; one for now. It lives here rather than on a code
    /// channel because it is a property of the machine's motion, not of who is commanding it: two
    /// channels feeding the same motion system have to agree about where the head is. Read and
    /// written under <see cref="Lock"/>, for the same reason <see cref="Builder"/> is
    /// </remarks>
    public MovementState State { get; } = new();

    /// <summary>
    /// Take the lock that orders move building
    /// </summary>
    /// <returns>The lock scope</returns>
    /// <remarks>
    /// Callers that need to read the state and then build a move from it must hold this across both,
    /// or another channel's move can be built in between and the delta is measured from the wrong
    /// place
    /// </remarks>
    public Lock.Scope Lock() => _lock.EnterScope();

    /// <summary>
    /// Re-read the machine description from the object model and push it down to the engine
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="adoptGeometryFromObjectModel">
    /// Take the geometry from the object model rather than keeping the one this planner holds. Only
    /// for the first configuration, before any M-code has selected one
    /// </param>
    /// <returns>True if the engine accepted it</returns>
    /// <remarks>
    /// Only safe while nothing is in flight: steps per mm changing under a queued move would make the
    /// endpoints it was planned against mean something different from what the drives will do. The
    /// caller is responsible for having drained the ring first
    /// </remarks>
    public async ValueTask<bool> ReconfigureAsync(CancellationToken cancellationToken = default,
                                                 bool adoptGeometryFromObjectModel = false)
    {
        MotionParameters parameters;
        byte[] configBuffer = new byte[MachineConfig.SerializedLength];
        int length;

        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (adoptGeometryFromObjectModel)
            {
                // Before any code has configured a geometry, whatever the object model already
                // describes is the best description of the machine there is - which matters when the
                // model was populated by something other than this process's M-codes
                Geometry = KinematicsFactory.Create(model.Move.Kinematics);
            }

            // The geometry keeps its own copy of M208's box, and this is where it follows the object
            // model. Separate from taking the snapshot, because it writes to the geometry
            MotionParameters.ApplyAxisLimits(model.Move, Geometry);

            parameters = MotionParameters.FromObjectModel(model.Move, Geometry);
            length = parameters.Config.Serialize(configBuffer);
        }

        // A driver claimed by two drives is a configuration fault with no visible symptom until an
        // endstop fires, at which point the stop is attributed to whichever drive the lookup
        // happens to answer with. Said here because this is where the description was read
        foreach (string conflict in parameters.DriverConflicts)
        {
            logger.LogWarning("Motion configuration: {Conflict}", conflict);
        }

        using (_lock.EnterScope())
        {
            if (!linkInterface.Native.ConfigureMotion(configBuffer.AsSpan(0, length)))
            {
                logger.LogError("The motion engine rejected the machine description");
                return false;
            }

            Builder.Reconfigure(parameters);

            // Motor positions are microstep counts, and the new description may convert them to a
            // different position in mm. Both sides have to be brought back to the position the
            // machine is actually at, or the next move is planned as a delta from somewhere else
            Builder.RecalculateEndPoints();
            PushPositionsToEngine();
            return true;
        }
    }

    /// <summary>
    /// Take a fresh snapshot of the object model without pushing anything to the motion engine
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <remarks>
    /// <para>
    /// For the settings a move carries with it - jerk limits, pressure advance, backlash, nonlinear
    /// extrusion, input shaping. The engine holds no copy of those to update, so there is nothing to
    /// push and nothing to synchronise: the next move built takes the new value and the moves already
    /// queued keep the one they were built with. That is what lets M572 and its like take effect
    /// mid-print without stopping the machine. See <c>docs/devel/MOTION_CONFIG_ORDERING.md</c>.
    /// </para>
    /// <para>
    /// Not for anything that changes what a microstep means. Steps per mm, driver mapping and
    /// geometry are held by the engine and describe moves that are already queued, so those go
    /// through <see cref="ReconfigureAsync"/> at standstill.
    /// </para>
    /// </remarks>
    public async ValueTask RefreshTuningAsync(CancellationToken cancellationToken = default)
    {
        MotionParameters parameters;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            MotionParameters.ApplyAxisLimits(model.Move, Geometry);
            parameters = MotionParameters.FromObjectModel(model.Move, Geometry);
        }

        using (_lock.EnterScope())
        {
            // No RecalculateEndPoints: nothing here changes what a microstep means, so where the
            // machine is has not moved under us
            Builder.Reconfigure(parameters);
        }
    }

    /// <summary>
    /// Wait until every ring has run out the moves it was given
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the machine reached standstill</returns>
    /// <remarks>
    /// The counterpart of RepRapFirmware's <c>LockAllMovementSystemsAndWaitForStandstill</c>. Codes
    /// that change what a microstep means - steps per mm, microstepping, driver mapping, geometry -
    /// must not take effect while a move planned under the old description is still running, because
    /// the endpoints it was planned against would be executed under the new one.
    /// <para>
    /// Flushing the code pipeline is not enough on its own: that only guarantees the moves have been
    /// submitted, not that they have been executed
    /// </para>
    /// </remarks>
    /// <summary>
    /// Whether the engine still has moves to run
    /// </summary>
    /// <remarks>
    /// <para>
    /// What <see cref="WaitForStandstillAsync"/> waits on, as a question rather than a wait. The
    /// rings report what has been scheduled and what has completed, and a difference is motion still
    /// to happen.
    /// </para>
    /// <para>
    /// A move that has been submitted but not yet taken up counts as motion, and has to. Submitting
    /// hands the move to a lock-free queue and returns; the ring only counts it as scheduled once
    /// the motion thread has taken it out, which is up to a tick later. Asking the rings alone in
    /// that window is answered "the machine is idle" about a move that has not started - and a
    /// homing move which believed that would decide its endstop never triggered before the axis had
    /// begun to move towards it
    /// </para>
    /// </remarks>
    public bool IsMoving
    {
        get
        {
            if (linkInterface.Native.HasPendingSubmissions)
            {
                return true;
            }

            for (int ring = 0; ring < MotionLimits.MaxRings; ring++)
            {
                // An unconfigured ring reads zero for both, so this is safe over all of them
                if (linkInterface.Native.GetScheduledMoves(ring) != linkInterface.Native.GetCompletedMoves(ring))
                {
                    return true;
                }
            }
            return false;
        }
    }

    public async ValueTask<bool> WaitForStandstillAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!IsMoving)
            {
                return true;
            }

            await Task.Delay(StandstillPollInterval, cancellationToken);
        }
        return false;
    }

    /// <summary>
    /// Build a move and hand it to the engine
    /// </summary>
    /// <param name="move">The move to queue</param>
    /// <returns>What became of it</returns>
    /// <remarks>
    /// Busy is not a failure: the engine's ring is full, which is the normal state when moves are
    /// being executed faster than they can be run. The caller waits and tries the same move again
    /// </remarks>
    public MoveSubmitResult QueueMove(RawMove move)
    {
        using (_lock.EnterScope())
        {
            if (!linkInterface.Native.CanAddMove(move.RingNumber))
            {
                return MoveSubmitResult.Busy;
            }

            if (move.MoveId == 0)
            {
                move.MoveId = NextMoveId();
            }

            // TODO RRF has a bed levelling move check (`Move::MoveLoop()`). It doesn't make sense in this function but the functionality will need to be ported.

            // Taken before the build, which advances the builder to the end of this move. If the
            // engine will not take the move, this is where the builder has to go back to
            Builder.EndPoints.CopyTo(_endPointsBeforeBuild);

            MoveBuildResult built = Builder.Build(move, _buffer);
            switch (built.Error)
            {
                case NativeMovementError.NoMovement:
                    return MoveSubmitResult.NoMovement;

                case NativeMovementError.Ok:
                    break;

                default:
                    logger.LogError("Move {MoveId} could not be built: {Error}", move.MoveId, built.Error);
                    return MoveSubmitResult.Rejected;
            }

            if (!linkInterface.Native.SubmitMove(_buffer.AsSpan(0, built.Length)))
            {
                // The builder has already advanced to the end of this move, so the submission cannot
                // simply be dropped - the next move would be planned from a position the machine was
                // never given. CanAddMove above makes this unlikely, but it is advisory, so the
                // builder goes back to where the last move that did go out left it.
                //
                // Not the engine's position: that is where the machine has *got to*, which is a whole
                // queue of moves behind the end of what has been planned. Resynchronising to it would
                // plan the retry of this very move from somewhere the queue has long passed
                logger.LogWarning("The motion engine refused move {MoveId} after accepting it; it will be retried", move.MoveId);
                Builder.ResyncEndpoints(_endPointsBeforeBuild);
                return MoveSubmitResult.Busy;
            }

            _lastSubmittedMoveId[move.RingNumber] = move.MoveId;
            PublishCommittedPosition();
            return MoveSubmitResult.Queued;
        }
    }

    /// <summary>
    /// Say in the object model where the move just queued leaves the machine
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>move.axes[].userPosition</c> is the target of the last move fed into the look-ahead, which
    /// is what the interpreter's own position is - so this is where it becomes visible. It is the
    /// counterpart of <c>machinePosition</c>, which <c>MotionService</c> publishes from the engine's
    /// live snapshot: one says where the machine has been told to go and the other where it has got
    /// to, and the two differ by however many moves are still queued.
    /// </para>
    /// <para>
    /// Published here rather than by the callers because there are three of them and this is the one
    /// thing they all do. It is also what G92 and a homing or probing move call once they have moved
    /// the interpreter without queueing anything, so there is one description of the projection
    /// rather than one per place that changes the position.
    /// </para>
    /// <para>
    /// Both locks have to be held on entry - the object model's for writing and the planner's for
    /// reading the state this projects. <c>AsyncReaderWriterLock</c> is non-reentrant with no way to
    /// ask whether it is held (§13.1), so that is a precondition rather than something this can check
    /// </para>
    /// </remarks>
    public void PublishCommittedPosition()
    {
        int numAxes = Parameters.SharedAxisCount(model.Move);
        int workplace = model.Move.MotionSystems.Count > 0 ? model.Move.MotionSystems[0].WorkplaceNumber : 0;

        for (int axis = 0; axis < numAxes; axis++)
        {
            DuetAPI.ObjectModel.Axis axisConfig = model.Move.Axes[axis];

            // The workplace offset is included in the interpreter's position and taken back off for
            // reporting, so the number a client reads is the one the operator typed
            float offset = workplace >= 0 && workplace < axisConfig.WorkplaceOffsets.Count
                           ? axisConfig.WorkplaceOffsets[workplace]
                           : 0.0f;
            axisConfig.UserPosition = State.CurrentUserPosition[axis] - offset;

            // Where the motors were told to end up, in microsteps. RepRapFirmware reports the live
            // count; these are the commanded endpoints, which is the same number once the queue has
            // drained and is the only one this side knows
            axisConfig.StepPos = Builder.EndPoints[axis];
        }

        // TODO move.extruders[].position and move.motionSystems[].virtualEPos need the extrusion
        // totals RepRapFirmware keeps - rawExtruderTotal and latestVirtualExtruderPosition - which
        // ApplyExtrusion does not track yet. Publishing the endpoints here would report filament
        // consumed in microsteps of one drive rather than the mm of filament a client expects
    }

    /// <summary>
    /// Say in the object model where the restore points are
    /// </summary>
    /// <remarks>
    /// <para>
    /// RepRapFirmware publishes the same points twice, and so does this: <c>state.restorePoints[]</c>
    /// is what a client reads and <c>move.motionSystems[].restorePoints[]</c> is the same list per
    /// motion system, which is where it will belong once there is more than one of them.
    /// </para>
    /// <para>
    /// Called after a point changes rather than on a timer, because they change rarely - a pause, a
    /// tool change, a G60 - and a projection that is rebuilt when nothing moved is patch traffic for
    /// no reader. Both locks have to be held on entry, as for <see cref="PublishCommittedPosition"/>
    /// </para>
    /// </remarks>
    public void PublishRestorePoints()
    {
        int numAxes = Parameters.SharedAxisCount(model.Move);

        // state.restorePoints is deprecated in favour of the per-motion-system copy, and is written
        // anyway because RepRapFirmware writes both and a client still reading the old one would
        // otherwise see a machine that never saves a restore point. Deprecation is a message to
        // clients about which to read, not a reason for the server to stop filling it
#pragma warning disable CS0618
        while (model.State.RestorePoints.Count < RestorePoint.NumVisible)
        {
            model.State.RestorePoints.Add(new DuetAPI.ObjectModel.RestorePoint());
        }
        if (model.Move.MotionSystems.Count == 0)
        {
            model.Move.MotionSystems.Add(new DuetAPI.ObjectModel.MotionSystem());
        }
        DuetAPI.ObjectModel.MotionSystem motionSystem = model.Move.MotionSystems[0];
        while (motionSystem.RestorePoints.Count < RestorePoint.NumVisible)
        {
            motionSystem.RestorePoints.Add(new DuetAPI.ObjectModel.RestorePoint());
        }

        for (int i = 0; i < RestorePoint.NumVisible; i++)
        {
            Project(State.RestorePoints[i], model.State.RestorePoints[i], numAxes);
            Project(State.RestorePoints[i], motionSystem.RestorePoints[i], numAxes);
        }
#pragma warning restore CS0618
    }

    /// <summary>
    /// Copy one restore point into its object model counterpart
    /// </summary>
    /// <param name="from">Restore point held by the interpreter</param>
    /// <param name="to">Object model copy</param>
    /// <param name="numAxes">How many axes are visible</param>
    /// <remarks>
    /// The file position, the proportion done and the arc start coordinates are not copied: they say
    /// how to resume the job rather than where the machine is, and RepRapFirmware does not publish
    /// them either
    /// </remarks>
    private static void Project(RestorePoint from, DuetAPI.ObjectModel.RestorePoint to, int numAxes)
    {
        while (to.Coords.Count < numAxes)
        {
            to.Coords.Add(0.0f);
        }
        for (int axis = 0; axis < numAxes; axis++)
        {
            to.Coords[axis] = from.Coords[axis];
        }

        to.ExtruderPos = from.VirtualExtruderPosition;
        to.FanPwm = from.FanSpeed;
        to.FeedRate = from.FeedRate;
        to.GCommandNumber = from.GCommandNumber;
        to.ToolNumber = from.ToolNumber;
    }

    /// <summary>
    /// Where each queued job move came from, so a feedhold can say where to resume
    /// </summary>
    /// <remarks>The caller must hold <see cref="Lock"/></remarks>
    public JobMoveIndex JobMoves { get; } = new();

    /// <summary>
    /// What a stop did
    /// </summary>
    /// <param name="Stopped">Whether the engine was brought to a planned stop</param>
    /// <param name="FirstPurgedMoveId">Id of the earliest move that was dropped, if any were</param>
    /// <param name="MovesPurged">How many moves were dropped</param>
    /// <param name="LastSurvivingMoveId">
    /// Id of the last move the stop left standing, which is the one the machine comes to rest on.
    /// This and not <paramref name="MovesPurged"/> is what says which of the work this side is owed
    /// will never happen: a stop that purges nothing from the ring still discards whatever was on its
    /// way to it, so the purge count can be zero while moves this side is waiting for are gone
    /// </param>
    /// <remarks>
    /// Motion facts only. What they mean for the job file is
    /// <see cref="TakeJobResumePoint"/>'s to say, because the file is not something the engine or
    /// this call knows anything about
    /// </remarks>
    public readonly record struct FeedholdOutcome(bool Stopped, uint FirstPurgedMoveId, uint MovesPurged,
                                                 uint LastSurvivingMoveId);

    /// <summary>
    /// Stop the machine before the queue has run, and drop the moves after the stopping point
    /// </summary>
    /// <param name="plannedDeceleration">
    /// False for RepRapFirmware's behaviour, which skips to a junction the toolpath is already slow
    /// enough to stop at and finds none during a fast print; true for the feedhold, which plans a
    /// deceleration at the first move the engine has not committed
    /// </param>
    /// <param name="interpreter">
    /// The interpreter whose position this has to put right. Passed in rather than left to the
    /// caller because it has to happen under the lock that the purge happens under: a move built in
    /// between would be measured from the end of a queue that no longer exists
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What the stop did</returns>
    /// <remarks>
    /// <para>
    /// The engine acts on this from its own thread, because dropping a move frees its segments. So
    /// this asks and then waits for the answer, which is the one place in the planner that waits on
    /// the motion thread rather than the other way round.
    /// </para>
    /// <para>
    /// Whatever it purges, this side's idea of where the machine is has to come back from the
    /// engine: the interpreter position ran ahead by however many moves were dropped, and a move
    /// built from it would start from somewhere the machine never reached
    /// </para>
    /// </remarks>
    public async ValueTask<FeedholdOutcome> StopEarlyAsync(bool plannedDeceleration, MoveInterpreter interpreter,
                                                           CancellationToken cancellationToken = default)
    {
        uint sequenceBefore;
        if (!linkInterface.Native.TryGetFeedholdResult(out sequenceBefore, out _, out _, out _, out _))
        {
            return new FeedholdOutcome(false, 0, 0, 0);
        }

        if (!linkInterface.Native.RequestStop(plannedDeceleration))
        {
            logger.LogWarning("The motion engine would not take a stop request; draining the queue instead");
            return new FeedholdOutcome(false, 0, 0, 0);
        }

        // As soon as the request is in, not when the answer comes back. The motion thread acts on it
        // in its own time, and a segmented move part-way out would otherwise spend that window
        // queueing segments the machine has just been told not to make. Nothing is lost if the
        // engine turns out not to have stopped: the submission gives up either way, and the pause
        // reads back how far it got
        using (Lock())
        {
            State.NotePurge();
        }

        // The motion thread acts on the request within one pass of its loop
        uint firstPurgedMoveId = 0, movesPurged = 0, lastSurvivingMoveId = 0;
        bool stopped = false;
        int[] restEndpoints = new int[MotionLimits.MaxAxesPlusExtruders];
        while (!cancellationToken.IsCancellationRequested)
        {
            if (linkInterface.Native.TryGetFeedholdResult(out uint sequence, out firstPurgedMoveId,
                                                          out movesPurged, out lastSurvivingMoveId,
                                                          out stopped, restEndpoints)
                && sequence != sequenceBefore)
            {
                break;
            }
            await Task.Delay(FeedholdPollInterval, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (!stopped)
        {
            return new FeedholdOutcome(false, 0, 0, 0);
        }

        // The read lock because putting the interpreter's position back reads the transform out of
        // the object model, and the planner lock because everything below is the state a move is
        // built from. This order everywhere: the object model first, the planner second
        using (await model.AccessReadOnlyAsync(cancellationToken))
        using (Lock())
        {
            // Both sides of the position ran ahead of the machine by everything that was dropped, so
            // both are fiction. What replaces them is where the machine will come to rest, which the
            // stop reports from the ring: the moves it could not recall are still running, so the
            // engine's commanded position is somewhere the machine is passing through rather than
            // the place it stops. The interpreter's follows from it - under this lock, so that the
            // next move built anywhere is measured from where the machine really ends up
            Builder.ResyncEndpoints(restEndpoints);
            interpreter.SyncInterpreterToMachine();

            // A segmented move that was part-way through submitting has had its remaining segments
            // dropped with the rest, so the claim on the ring goes with them
            State.SegmentsLeft = 0;
        }

        logger.LogInformation("Stopped the machine early, dropping {Count} queued move(s)", movesPurged);
        return new FeedholdOutcome(true, firstPurgedMoveId, movesPurged, lastSurvivingMoveId);
    }

    /// <summary>
    /// Take where a resume would have to carry the job on from
    /// </summary>
    /// <param name="held">What the stop did, or the default if there was no stop</param>
    /// <returns>The resume point, or null to resume from the last completed job code</returns>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's three branches of <c>DoAsynchronousPause</c> (GCodes.cpp:1086), each of which
    /// fills in the file position and the proportion together and then calls <c>ClearMove</c>:
    /// wherever moves were dropped the ring names the boundary, and where none were the code that was
    /// still going out does, because everything queued was already committed and will run.
    /// </para>
    /// <para>
    /// Called once per pause, and before the job's read-ahead is cancelled. Taking the record is what
    /// fixes the segment count in it: a submission that finds the record is no longer its own queues
    /// nothing more, so what is read here stays true however that submission then unwinds
    /// </para>
    /// </remarks>
    public JobResumePoint? TakeJobResumePoint(FeedholdOutcome held)
    {
        using (Lock())
        {
            JobMoveOrigin? current = State.CurrentJobMove;
            State.CurrentJobMove = null;

            JobResumePoint? resume;
            if (held.MovesPurged > 0)
            {
                // The earliest dropped move names the boundary. When it cannot be named it was a
                // macro's, so the job's own code had not started and the resume rewinds to the macro
                // invocation, which is the last job code that completed
                resume = JobMoves.TryGet(held.FirstPurgedMoveId, out JobMoveOrigin origin, out int segment)
                    ? origin.PointAt(segment)
                    : null;
            }
            else
            {
                resume = current?.PointAt(current.SegmentsQueued);
            }

            JobMoves.Clear();
            return resume;
        }
    }

    /// <summary>
    /// How often to ask whether the motion thread has acted on a stop request
    /// </summary>
    private static readonly TimeSpan FeedholdPollInterval = TimeSpan.FromMilliseconds(2);

    /// <summary>
    /// Tell the engine where the machine is, from the endpoints the builder holds
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other direction from <see cref="ResyncFromEngine"/>, and the one every code that redefines
    /// the position without moving anything has to take: G92, the coordinate a homing move adopts
    /// from its switch, the height a probing move measures. Until the engine is told, it still holds
    /// the position the last move was planned to end at, and it plans the next move as the difference
    /// between that and what it is given - so the machine would travel the gap between where it has
    /// been told it is and where it was last commanded to be.
    /// </para>
    /// <para>
    /// RepRapFirmware's <c>Move::ChangeEndpointsAfterHoming</c>, which is the same pair of updates
    /// for the same reason. Axes only: an extruder's motion is relative and the engine carries the
    /// fraction of a step between moves, so an endpoint would lose it. The caller must hold
    /// <see cref="Lock"/>
    /// </para>
    /// </remarks>
    public void PushPositionsToEngine()
    {
        int numAxes = Parameters.NumAxes;
        uint driveMask = (1u << Math.Min(numAxes, MotionLimits.MaxAxesPlusExtruders)) - 1;
        if (!linkInterface.Native.SetMotorPositions(driveMask, Builder.EndPoints))
        {
            // The two sides now disagree about where the machine is, and the engine's copy is what
            // the drives will be given. Nothing here can put that right, so it is said out loud
            logger.LogError("The motion engine would not take the machine position; it and the planner have diverged");
        }
    }

    /// <summary>
    /// Take the machine position from the engine's live snapshot
    /// </summary>
    /// <remarks>
    /// The fallback when this side's idea of where the machine is has become untrustworthy. The
    /// snapshot is what the drives were last told, so it is authoritative - but only at standstill.
    /// It advances a segment at a time, so while a move is running it reads somewhere the machine is
    /// passing through; a stop that has left moves running takes the resting endpoints the stop
    /// itself reports instead, which is what <see cref="StopEarlyAsync"/> does
    /// </remarks>
    public void ResyncFromEngine()
    {
        if (linkInterface.Native.GetMotorPositions(_resyncBuffer, out _) > 0)
        {
            Builder.ResyncEndpoints(_resyncBuffer);
        }
    }

    /// <summary>
    /// Id of the last move handed to the engine on the given ring, or 0 if none has been
    /// </summary>
    /// <param name="ring">Ring to read</param>
    /// <returns>Move id, or 0</returns>
    /// <remarks>
    /// The anchor of a deferred code: the point in the path its effect belongs after is the end of
    /// the last move submitted when the code was read. Moves the engine discarded
    /// (<see cref="MoveSubmitResult.NoMovement"/>) never become the anchor, because they never
    /// retire - which is what RepRapFirmware's <c>segmentsLeft == 0</c> gate protects against
    /// </remarks>
    public uint LastSubmittedMoveId(int ring)
    {
        using (_lock.EnterScope())
        {
            return (ring >= 0 && ring < _lastSubmittedMoveId.Length) ? _lastSubmittedMoveId[ring] : 0;
        }
    }

    /// <summary>
    /// The next correlation id
    /// </summary>
    /// <returns>A move id, never zero</returns>
    private uint NextMoveId()
    {
        uint id = _nextMoveId++;
        if (_nextMoveId == 0)
        {
            _nextMoveId = 1;                // zero means "no id" on the wire
        }
        return id;
    }
}
