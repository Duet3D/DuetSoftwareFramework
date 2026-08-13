using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Link;
using DuetControlServer.Motion;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// The half of arming an endstop that has to reach the boards before the move runs
/// </summary>
/// <remarks>
/// <para>
/// RepRapFirmware arms an endstop in one place: <c>PrimeAxis</c> is virtual on <c>Endstop</c>, each
/// subclass does its own CAN work in it, and <c>EnableAxisEndstops</c> is one loop over one call. Here
/// it takes two, because telling a driver what speed to expect is a CAN round trip and the move is
/// built inside a synchronous lock that nothing may await across.
/// </para>
/// <para>
/// So this is the phase that may await, and <see cref="EndstopArming"/> is the phase that may not.
/// Both dispatch through <see cref="IEndstopKind"/> and both read the same
/// <see cref="EndstopPlan"/>, which is what keeps a driver the boards were armed for and a driver the
/// move tells the controller to watch from being two different answers
/// </para>
/// </remarks>
internal sealed partial class GCodeHandler
{
    /// <summary>
    /// Work out what each axis the move names watches
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>One plan per axis the code names</returns>
    /// <exception cref="GCodeException">An axis the code names has no endstop</exception>
    /// <remarks>
    /// Separate from <see cref="PrepareEndstopsAsync"/> so that the plans exist before anything has
    /// been sent: a board that refuses to arm throws, and the release still has to know which kinds
    /// to undo. The speeds are worked out from the code rather than from the built move because
    /// arming has to reach the boards before the move is built - RepRapFirmware does the same, and
    /// its own comment says the calculation is an approximation that duplicates
    /// <c>DDA::InitStandardMove</c>, because all the driver needs is the order of magnitude
    /// </remarks>
    private async ValueTask<List<EndstopPlan>> PlanEndstopsAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            int numAxes = planner.Parameters.SharedAxisCount(model.Move);
            return EndstopPlanner.Plan(code, model.Move, model.Sensors, planner.Parameters.Geometry, numAxes,
                                       planner.Parameters.StepsPerMm, HomingSpeed(code, numAxes));
        }
    }

    /// <summary>
    /// Tell the boards what to watch for
    /// </summary>
    /// <param name="plans">What each axis watches</param>
    /// <param name="state">Receives what was armed, so that it can be released afterwards</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Anything the boards had to say about being armed</returns>
    /// <exception cref="GCodeException">A board refused to arm, so the move must not run</exception>
    /// <remarks>Outside the model lock, because arming is a CAN round trip</remarks>
    private async ValueTask<Message> PrepareEndstopsAsync(IReadOnlyList<EndstopPlan> plans, EndstopArmingState state,
                                                          CancellationToken cancellationToken)
    {
        List<Message> replies = [];
        foreach (EndstopPlan plan in plans)
        {
            IEndstopKind? kind = EndstopKinds.For(plan.Kind);
            if (kind is not null)
            {
                // Some boards may already have been armed, so the caller still has to release
                // whatever it got back
                replies.Add(await kind.PrepareAsync(plan, state, linkInterface, cancellationToken));
            }
        }
        return replies.ToMessage();
    }

    /// <summary>
    /// Undo what <see cref="PrepareEndstopsAsync"/> sent, however the move ended
    /// </summary>
    /// <param name="plans">The move's plans</param>
    /// <param name="state">What was armed</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An awaitable task</returns>
    /// <remarks>
    /// It has to happen however the move ended: a driver left armed would report a stall during an
    /// ordinary move, and the next move that named the stall handle would stop on it
    /// </remarks>
    private async ValueTask ReleaseEndstopsAsync(IReadOnlyList<EndstopPlan> plans, EndstopArmingState state,
                                                  CancellationToken cancellationToken)
    {
        if (plans.Count == 0)
        {
            return;                             // an ordinary move, which armed nothing
        }

        foreach (IEndstopKind kind in EndstopKinds.Used(plans))
        {
            await kind.ReleaseAsync(state, linkInterface, logger, cancellationToken);
        }
    }

    /// <summary>
    /// About how fast a homing move will run
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <returns>Speed in mm/sec</returns>
    /// <remarks>
    /// The feed rate the move will use. RepRapFirmware works out each axis' share of it from the
    /// movement amounts, but a homing move is one axis or a coupled set of them going one way, so its
    /// share is the whole feed rate - and RRF's own comment says it assumes the move was not commanded
    /// faster than the axes can go. Taken from the code rather than the built move so that this can
    /// run before the object model lock is taken, since arming is a CAN round trip
    /// </remarks>
    private float HomingSpeed(Commands.Code code, int numAxes)
    {
        InputChannel? input = model.Inputs[code.Channel];
        float feedRate = code.TryGetFloat('F', out float f) ? f : input?.FeedRate ?? 0.0f;

        bool rotationalOnly = true;
        for (int axis = 0; axis < numAxes; axis++)
        {
            Axis axisConfig = model.Move.Axes[axis];
            if (code.HasParameter(axisConfig.Letter) && !axisConfig.Rotational)
            {
                rotationalOnly = false;
                break;
            }
        }

        float unitScale = !rotationalOnly && input?.DistanceUnit == DistanceUnit.Inch ? MmPerInch : 1.0f;
        return feedRate * unitScale / SecondsPerMinute;
    }
}
