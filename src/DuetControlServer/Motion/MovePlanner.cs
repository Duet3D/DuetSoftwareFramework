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
    private uint _nextMoveId = 1;

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
        byte[] configBuffer = new byte[MotionConfig.SerializedLength];
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
            uint driveMask = parameters.NumAxes >= 32 ? uint.MaxValue : (1u << parameters.NumAxes) - 1;
            linkInterface.Native.SetMotorPositions(driveMask, Builder.EndPoints);
            return true;
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
    public async ValueTask<bool> WaitForStandstillAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            bool moving = false;
            for (int ring = 0; ring < MotionLimits.MaxRings; ring++)
            {
                // An unconfigured ring reads zero for both, so this is safe over all of them
                if (linkInterface.Native.GetScheduledMoves(ring) != linkInterface.Native.GetCompletedMoves(ring))
                {
                    moving = true;
                    break;
                }
            }

            if (!moving)
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
            // TODO nothing writes move.axes[].userPosition, .stepPos, extruders[].position or
            // motionSystems[].virtualEPos, so M114 and the interfaces report zeros for all of them.
            // MovementState and the builder's endpoints already hold the answers - §15.2

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
                // simply be dropped - the machine would be planned from a position it never reached.
                // CanAddMove above makes this unlikely, but it is advisory, so put the builder back
                logger.LogWarning("The motion engine refused move {MoveId} after accepting it; resynchronising", move.MoveId);
                ResyncFromEngine();
                return MoveSubmitResult.Busy;
            }

            return MoveSubmitResult.Queued;
        }
    }

    /// <summary>
    /// Take the machine position from the engine's live snapshot
    /// </summary>
    /// <remarks>
    /// The fallback when this side's idea of where the machine is has become untrustworthy. The
    /// snapshot is what the drives were last told, so it is authoritative
    /// </remarks>
    public void ResyncFromEngine()
    {
        if (linkInterface.Native.GetMotorPositions(_resyncBuffer, out _) > 0)
        {
            Builder.ResyncEndpoints(_resyncBuffer);
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
