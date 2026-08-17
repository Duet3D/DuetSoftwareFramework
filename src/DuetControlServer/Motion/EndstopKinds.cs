using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Motion.Native;
using Microsoft.Extensions.Logging;

namespace DuetControlServer.Motion;

/// <summary>
/// What one move has already sent to the boards, so that it can be undone
/// </summary>
/// <remarks>
/// Held by the move rather than by the endstop, because the endstops of the object model are
/// replicated data with no lifetime of their own: an <c>M574</c> between two moves replaces the
/// object that would have had to remember what the first one armed
/// </remarks>
internal sealed class EndstopArmingState
{
    /// <summary>Boards this move sent an arming message to, and must therefore release</summary>
    public HashSet<byte> ArmedBoards { get; } = [];

    /// <summary>
    /// Probes this move raised to the probing report rate, and must therefore slow back down
    /// </summary>
    /// <remarks>
    /// A list rather than a set because releasing twice costs a message and no correctness, while a
    /// probe missed here is one left reporting at the probing rate for the rest of the job
    /// </remarks>
    public List<ProbeArming.ProbeMonitor> ArmedProbes { get; } = [];
}

/// <summary>
/// One kind of endstop, and everything a move does about it
/// </summary>
/// <remarks>
/// <para>
/// RepRapFirmware's <c>Endstop</c> vtable: <c>PrimeAxis</c> is virtual and each subclass does its own
/// CAN work in it, so <c>EnableAxisEndstops</c> is one loop over one call whatever kind the axis has.
/// The same shape here, split in two because <see cref="TryArm"/> runs inside the planner lock and
/// nothing there may await - see <see cref="EndstopPlanner"/>.
/// </para>
/// <para>
/// Both halves live on the one type so that adding a kind is implementing an interface rather than
/// remembering two call sites
/// </para>
/// </remarks>
internal interface IEndstopKind
{
    /// <summary>
    /// Whether this handles the given endstop type
    /// </summary>
    /// <param name="type">The endstop type</param>
    /// <returns>True if it does</returns>
    /// <remarks>
    /// A predicate rather than a property because one kind may cover more than one type. Motor stall
    /// is currently one kind for both <c>S3</c> and <c>S4</c>, which is the defect §4.3 of the plan
    /// describes; splitting them is splitting this
    /// </remarks>
    bool Handles(EndstopType type);

    /// <summary>
    /// Whether a move watching this has to be run at the reduced acceleration <c>M201.1</c> configures
    /// </summary>
    /// <remarks>RepRapFirmware's <c>Endstop::ShouldReduceAcceleration</c></remarks>
    bool ReducesAcceleration { get; }

    /// <summary>
    /// Tell the boards what to watch for, before the move is built
    /// </summary>
    /// <param name="plan">What this axis watches</param>
    /// <param name="state">What this move has armed so far, to be added to</param>
    /// <param name="link">Link interface</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Anything the boards had to say that is worth passing on</returns>
    /// <exception cref="GCodeException">A board refused, so the move must not run</exception>
    ValueTask<Message> PrepareAsync(EndstopPlan plan, EndstopArmingState state, LinkInterface link,
                                    CancellationToken cancellationToken);

    /// <summary>
    /// Undo <see cref="PrepareAsync"/>, however the move ended
    /// </summary>
    /// <param name="state">What this move armed</param>
    /// <param name="link">Link interface</param>
    /// <param name="logger">Logger, because there is nobody left to report a failure to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An awaitable task</returns>
    ValueTask ReleaseAsync(EndstopArmingState state, LinkInterface link, ILogger logger,
                           CancellationToken cancellationToken);

    /// <summary>
    /// Write what stops this axis into the move
    /// </summary>
    /// <param name="plan">What this axis watches</param>
    /// <param name="stopInput">The stop input to fill in</param>
    /// <returns>Null if the axis is armed, else why its endstop cannot stop a move</returns>
    /// <remarks>Runs inside the planner lock, so it must not await and must not touch the bus</remarks>
    string? TryArm(EndstopPlan plan, MoveStopInput stopInput);
}

/// <summary>
/// The kind of endstop an axis has
/// </summary>
/// <remarks>
/// The dispatch RepRapFirmware gets from a vtable. It cannot hang off
/// <see cref="DuetAPI.ObjectModel.Endstop"/> itself: that is a replicated object-model class, so
/// behaviour on it would cross the API boundary and be visible to every client
/// </remarks>
internal static class EndstopKinds
{
    private static readonly IEndstopKind[] All =
    [
        new SwitchEndstopKind(),
        new ZProbeEndstopKind(),
        new StallEndstopKind()
    ];

    /// <summary>
    /// The kind that handles an endstop type
    /// </summary>
    /// <param name="type">The endstop type</param>
    /// <returns>The kind, or null if a move cannot be stopped by that type</returns>
    public static IEndstopKind? For(EndstopType type)
    {
        foreach (IEndstopKind kind in All)
        {
            if (kind.Handles(type))
            {
                return kind;
            }
        }
        return null;
    }

    /// <summary>
    /// Every kind the move's plans need, without repeats
    /// </summary>
    /// <param name="plans">The move's plans</param>
    /// <returns>The distinct kinds</returns>
    /// <remarks>
    /// What <see cref="IEndstopKind.ReleaseAsync"/> is called on. Releasing is per move rather than
    /// per axis - one message disables every stall endstop on a board - so two axes of the same kind
    /// must not release twice
    /// </remarks>
    public static IEnumerable<IEndstopKind> Used(IReadOnlyList<EndstopPlan> plans)
    {
        List<IEndstopKind> used = [];
        foreach (EndstopPlan plan in plans)
        {
            if (For(plan.Kind) is IEndstopKind kind && !used.Contains(kind))
            {
                used.Add(kind);
            }
        }
        return used;
    }
}

/// <summary>
/// A switch on an input pin, which is almost every endstop
/// </summary>
/// <remarks>
/// Nothing is sent per move, and nothing should be. The board was asked to watch the pin by
/// <c>M574</c> and has reported every change since, so the move only has to name the handle.
///
/// RepRapFirmware's <c>SwitchEndstop::PrimeAxis</c> opens every homing move with a CAN round trip
/// per switch, to refresh a cached level. There is no such cache here: the reports maintain
/// <c>sensors.endstops[].triggered</c> continuously, because <c>M119</c> and the already-closed
/// check read it at moments no move chose. Fetching it per move would add latency to learn something
/// already known - see §2.3 of the design differences article
/// </remarks>
internal sealed class SwitchEndstopKind : IEndstopKind
{
    /// <inheritdoc/>
    public bool Handles(EndstopType type) => type == EndstopType.InputPin;

    /// <inheritdoc/>
    public bool ReducesAcceleration => false;

    /// <inheritdoc/>
    public ValueTask<Message> PrepareAsync(EndstopPlan plan, EndstopArmingState state, LinkInterface link,
                                           CancellationToken cancellationToken)
        => new(new Message());

    /// <inheritdoc/>
    public ValueTask ReleaseAsync(EndstopArmingState state, LinkInterface link, ILogger logger,
                                  CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    /// <inheritdoc/>
    /// <remarks>
    /// An axis with one switch stops the whole drive on it, because every motor watches the same
    /// switch and none of them has one to run on to. An axis with a switch per driver stops each
    /// motor on its own, which is what squares a gantry, and the controller escalates the last of
    /// them to stopping the drive - RepRapFirmware's <c>numPortsLeftToTrigger == 1</c>
    /// </remarks>
    public string? TryArm(EndstopPlan plan, MoveStopInput stopInput)
    {
        if (!RemoteEndstops.TryGetStopInput(plan.Endstop, plan.Axis, plan.NumAxisDrivers, stopInput))
        {
            return "its endstop has no port assigned";
        }

        stopInput.Action = stopInput.NumSwitches > 1 ? StopAction.Driver : StopAction.Group;
        return null;
    }
}

/// <summary>
/// The Z probe standing in for the axis' endstop
/// </summary>
/// <remarks>
/// <para>
/// The pin is already watched - <c>M558</c> registered it under a probe handle - so unlike a stall
/// there is no handle to create. What is sent per move is what <see cref="ProbeArming"/> sends around
/// a tap: the threshold, and the interval that decides how quickly a change is reported.
/// </para>
/// <para>
/// A probe is created reporting at the idle rate, because a configured probe nobody is using has no
/// business filling the bus, so a homing move that does not arm it would be stopped up to
/// <see cref="ProbeArming.InactiveReportInterval"/> late. RepRapFirmware leaves this undone - its
/// <c>ZProbeEndstop::PrimeAxis</c> is a comment saying a remote probe ought to be checked here - and
/// gets away with it only until the first <c>G30</c>, which is what puts its probe on the idle rate
/// for good
/// </para>
/// </remarks>
internal sealed class ZProbeEndstopKind : IEndstopKind
{
    /// <inheritdoc/>
    public bool Handles(EndstopType type) => type == EndstopType.ZProbeAsEndstop;

    /// <inheritdoc/>
    public bool ReducesAcceleration => false;

    /// <inheritdoc/>
    public async ValueTask<Message> PrepareAsync(EndstopPlan plan, EndstopArmingState state, LinkInterface link,
                                                 CancellationToken cancellationToken)
    {
        if (plan.ProbeMonitor is not ProbeArming.ProbeMonitor monitor)
        {
            return new Message();               // no input to tell anything, so nothing to say to it
        }

        // Recorded before it is sent, because arming stops at the first refusal and whatever went out
        // before it still has to be undone
        state.ArmedProbes.Add(monitor);
        return await ProbeArming.StartAsync(monitor, link, cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask ReleaseAsync(EndstopArmingState state, LinkInterface link, ILogger logger,
                                        CancellationToken cancellationToken)
    {
        foreach (ProbeArming.ProbeMonitor monitor in state.ArmedProbes)
        {
            await ProbeArming.StopAsync(monitor, link, logger, cancellationToken);
        }
    }

    /// <inheritdoc/>
    public string? TryArm(EndstopPlan plan, MoveStopInput stopInput)
    {
        // One probe for the drive, so there is nothing for a motor to run on to alone. That is
        // RepRapFirmware's ZProbeEndstop, which stops the axis; EndstopArming raises it to the whole
        // move where the kinematics couples the drives
        if (plan.Probe is null ||
            !RemoteProbes.TryGetStopInput(plan.Probe, plan.Endstop.Probe ?? 0, StopAction.Group, stopInput))
        {
            return "its endstop is a Z probe that cannot stop a move; check M558";
        }
        return null;
    }
}

/// <summary>
/// The drivers of the axis stalling
/// </summary>
/// <remarks>
/// The one kind with something to send per move. A driver decides it has stalled by comparing the
/// back-EMF against what the commanded speed implies, so it cannot detect one until it has been told
/// what speed this move will run at - and must be untold afterwards, or it reports a stall during an
/// ordinary move. RepRapFirmware's <c>StallDetectionEndstop::PrimeAxis</c> by way of
/// <c>CanInterface::EnableRemoteStallEndstop</c>
/// </remarks>
internal sealed class StallEndstopKind : IEndstopKind
{
    /// <inheritdoc/>
    public bool Handles(EndstopType type)
        => type is EndstopType.MotorStallAny or EndstopType.MotorStallIndividual;

    /// <inheritdoc/>
    public bool ReducesAcceleration => true;

    /// <inheritdoc/>
    public ValueTask<Message> PrepareAsync(EndstopPlan plan, EndstopArmingState state, LinkInterface link,
                                          CancellationToken cancellationToken)
        => StallArming.ArmAsync(plan.DriversWatched, state, link, cancellationToken);

    /// <inheritdoc/>
    public ValueTask ReleaseAsync(EndstopArmingState state, LinkInterface link, ILogger logger,
                                  CancellationToken cancellationToken)
        => StallArming.ReleaseAsync(state, link, logger, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// This is the whole difference between <c>M574 S3</c> and <c>S4</c>. <c>MotorStallAny</c> stops
    /// every motor of the drive on any of them stalling; <c>MotorStallIndividual</c> stops each motor
    /// where it stalled, which is what squares a gantry, and the controller escalates the last of
    /// them to stopping the drive - RepRapFirmware's <c>individualMotors &amp;&amp; numDriversLeft > 1</c>
    /// </remarks>
    public string? TryArm(EndstopPlan plan, MoveStopInput stopInput)
    {
        if (!RemoteEndstops.TryGetStallStopInput(plan.DriversWatched, stopInput))
        {
            return "no driver is assigned to it";
        }

        stopInput.Action = plan.Kind == EndstopType.MotorStallIndividual ? StopAction.Driver : StopAction.Group;
        return null;
    }
}
