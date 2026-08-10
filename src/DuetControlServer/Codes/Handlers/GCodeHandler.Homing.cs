using DuetAPI.ObjectModel;
using System;
using DuetControlServer.Motion;
using DuetControlServer.Motion.Kinematics;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// Homing: the moves that stop on an endstop, and the code that runs the macros containing them
/// </summary>
/// <remarks>
/// A homing move is an ordinary move that the controller cuts short. What makes it homing is what
/// happens afterwards: the axis is at its switch, so its position is known however wrong it was
/// before, and that is the whole point of the operation
/// </remarks>
internal sealed partial class GCodeHandler
{
    /// <summary>
    /// How often to re-check whether the boards have finished winding back
    /// </summary>
    private static readonly TimeSpan RevertPollInterval = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Wait for a special move to finish and find out where it left the machine
    /// </summary>
    /// <param name="moveType">The H parameter the move was given</param>
    /// <param name="armedAxes">Axes the move was armed for, empty if it watched no endstop</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An awaitable task</returns>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>waitingForSpecialMoveToComplete</c> state. Every <c>G1 H</c> move waits,
    /// not only one that watches an endstop: it may stop short, and even an H2 that does not is
    /// planned in motor coordinates the interpreter's own position knows nothing about. Interpreting
    /// the next code before the machine has settled would measure it from a position that was never
    /// reached.
    /// </para>
    /// <para>
    /// The move ended wherever the endstop fired, which the engine has already corrected for the
    /// latency of the report. That position is not what the axis is set to, though: the switch is at
    /// a known place, so the axis takes the coordinate of the switch and everything else follows from
    /// it. RepRapFirmware does the same in its <c>checkingEndstops</c> state.
    /// </para>
    /// <para>
    /// Only an axis whose endstop actually triggered is homed. A move that ran to its full length
    /// without hitting anything leaves the axis where it was and unhomed, which is what makes a
    /// failed homing move visible rather than silently believed
    /// </para>
    /// </remarks>
    private async ValueTask FinishSpecialMoveAsync(MoveType moveType, IReadOnlyList<int> armedAxes,
                                                   CancellationToken cancellationToken)
    {
        await planner.WaitForStandstillAsync(cancellationToken);

        // Draining the rings is not enough on its own. A move that stopped short leaves the boards
        // winding back the overshoot, and that corrective move is synthesised on the board from the
        // revert message - the engine never scheduled it, so its ring counters never see it. Letting
        // the next move go out now would have the two overlap on the same driver. RepRapFirmware
        // waits the same time for the same reason, in CanMotion::RevertStoppedDrivers
        while (endstopCorrection.IsReverting && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(RevertPollInterval, cancellationToken);
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            // The engine's snapshot is where the drives actually are, which after an endstop stop is
            // not where the move was planned to end. Taken under the planner's lock because the
            // builder's idea of the position is what the next move is measured from
            using (planner.Lock())
            {
                planner.ResyncFromEngine();

                foreach (int axis in armedAxes)
                {
                    if (axis >= model.Move.Axes.Count)
                    {
                        continue;
                    }

                    Endstop? endstop = axis < model.Sensors.Endstops.Count ? model.Sensors.Endstops[axis] : null;
                    if (endstop is null || !endstop.Triggered)
                    {
                        continue;               // the move ran its full length, so nothing is known
                    }

                    switch (moveType)
                    {
                        case MoveType.Homing:
                            AdoptEndstopPosition(axis, endstop.HighEnd);
                            model.Move.Axes[axis].Homed = true;
                            break;

                        case MoveType.SenseLength:
                            // H3 asks how long the axis turned out to be rather than where it is, so
                            // the answer goes into the limit and the axis is left unhomed
                            RecordAxisLength(axis, endstop.HighEnd);
                            break;

                        default:
                            // H4 is a probing move; the probe path owns what comes of it. H0 and H2
                            // never arm an axis, so they never reach here
                            break;
                    }
                }

                // The interpreter's position was left alone while the special move ran, because a
                // motor position is not an axis position. Now that the machine has settled somewhere
                // definite, it has to be brought back into step with it - which is the one direction
                // the inverse transform is for
                SyncInterpreterToMachine();
            }
        }
    }

    /// <summary>
    /// Put an axis where its endstop says it is
    /// </summary>
    /// <param name="axis">Axis whose endstop fired</param>
    /// <param name="highEnd">Whether that endstop is at the high end of travel</param>
    /// <remarks>
    /// <para>
    /// Which way round this works is the geometry's decision. Where the endstop belongs to an axis,
    /// homing knows a coordinate and the motor endpoints follow from it. Where it belongs to a drive -
    /// a delta tower, a SCARA joint, a polar radius arm - what is known is that motor's position, and
    /// the axis coordinates follow from all of them together. A delta's carriage height is not an axis
    /// coordinate at all, so setting the axis to its limit would put the machine somewhere it has
    /// never been.
    /// </para>
    /// <para>
    /// RepRapFirmware splits the same two cases in <c>waitingForSpecialMoveToComplete</c>, and asks
    /// the kinematics for the position either way - see
    /// <see cref="KinematicsEngine.GetEndstopPosition"/>
    /// </para>
    /// </remarks>
    private void AdoptEndstopPosition(int axis, bool highEnd)
    {
        MotionParameters parameters = planner.Parameters;
        Axis axisConfig = model.Move.Axes[axis];

        float position = parameters.Geometry.GetEndstopPosition(
            axis, highEnd, axisConfig.Min, axisConfig.Max,
            planner.Builder.EndPoints, parameters.StepsPerMm);

        if (parameters.Geometry.HomesIndividualDrives)
        {
            float stepsPerMm = axis < parameters.StepsPerMm.Length ? parameters.StepsPerMm[axis] : 0.0f;
            if (stepsPerMm != 0.0f)
            {
                planner.Builder.SetDriveEndpoint(axis, (int)MathF.Round(position * stepsPerMm));
            }
        }
        else
        {
            planner.Builder.SetAxisPosition(axis, position);
        }
    }

    /// <summary>
    /// Record how long an axis turned out to be (G1 H3)
    /// </summary>
    /// <param name="axis">Axis that was measured</param>
    /// <param name="highEnd">Whether its endstop is at the high end of travel</param>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>axesToSenseLength</c> handling: the position the move stopped at becomes
    /// the axis limit, which is what M208 would otherwise have to be told by hand. The axis is
    /// deliberately not marked homed - knowing where the end is is not the same as knowing where the
    /// head is.
    /// </para>
    /// <para>
    /// The geometry keeps its own copy of the M208 box, and it is what every move is limited against,
    /// so writing only the object model would leave moves clamped to the travel the axis was assumed
    /// to have until the next code that rebuilds the whole description. M208 goes through
    /// <see cref="MovePlanner.ReconfigureAsync"/> and gets that for free; this does not, so it
    /// updates the copy itself
    /// </para>
    /// </remarks>
    private void RecordAxisLength(int axis, bool highEnd)
    {
        float stoppedAt = planner.Builder.StartCoordinates[axis];
        Axis axisConfig = model.Move.Axes[axis];

        if (highEnd)
        {
            axisConfig.Max = stoppedAt;
        }
        else
        {
            axisConfig.Min = stoppedAt;
        }

        planner.Parameters.SetAxisLimits(axis, axisConfig.Min, axisConfig.Max);
    }

    /// <summary>
    /// G28: home the machine
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// <para>
    /// Nothing here knows how to home anything. The machine's own macros do, and this runs them: ask
    /// the kinematics which macro comes next, run it, see which axes it homed, repeat. That loop is
    /// RepRapFirmware's <c>homing1</c> and <c>homing2</c> states, and it is a loop rather than a list
    /// because a macro is free to home more axes than it was asked for - <c>homeall.g</c> homes
    /// everything in one pass.
    /// </para>
    /// <para>
    /// A pass that homes nothing ends the operation with an error. Without that a missing switch or a
    /// mis-set endstop would spin here forever, and a machine that believes it is homed when it is
    /// not is worse than one that says it failed
    /// </para>
    /// </remarks>
    private async ValueTask<Message> HandleHomeAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!await planner.WaitForStandstillAsync(cancellationToken))
        {
            throw new OperationCanceledException();
        }

        uint toBeHomed = 0;
        char[] axisLetters;

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (model.Move.Axes.Count == 0)
            {
                return new Message(MessageType.Error, "No axes have been configured");
            }

            axisLetters = new char[model.Move.Axes.Count];
            for (int axis = 0; axis < model.Move.Axes.Count; axis++)
            {
                axisLetters[axis] = model.Move.Axes[axis].Letter;
                if (code.HasParameter(axisLetters[axis]))
                {
                    toBeHomed |= 1u << axis;
                }
            }

            if (toBeHomed == 0)
            {
                // G28 with no axes homes everything, so everything stops being homed first
                toBeHomed = axisLetters.Length >= 32 ? uint.MaxValue : (1u << axisLetters.Length) - 1;
            }

            // Marked not homed before rather than after: if homing is interrupted half way, the
            // machine must not still claim to know where the axes it was working on are
            for (int axis = 0; axis < model.Move.Axes.Count; axis++)
            {
                if ((toBeHomed & (1u << axis)) != 0)
                {
                    model.Move.Axes[axis].Homed = false;
                }
            }
        }

        while (toBeHomed != 0)
        {
            (string fileName, uint mustHomeFirst) = await NextHomingFileAsync(toBeHomed, axisLetters, cancellationToken);
            if (mustHomeFirst != 0)
            {
                return new Message(MessageType.Error,
                    $"Must home axes [{DescribeAxes(mustHomeFirst, axisLetters)}] "
                    + $"before homing [{DescribeAxes(toBeHomed, axisLetters)}]");
            }

            if (!await macroRunner.TryRunAsync(code.Channel, fileName, code, cancellationToken: cancellationToken))
            {
                return new Message(MessageType.Error, $"Homing file {fileName} not found");
            }

            uint homedNow = await GetHomedAxesAsync(cancellationToken);
            if ((toBeHomed & homedNow) == 0)
            {
                return new Message(MessageType.Error, $"Failed to home axes [{DescribeAxes(toBeHomed, axisLetters)}]");
            }
            toBeHomed &= ~homedNow;
        }
        return new Message();
    }

    /// <summary>
    /// Ask the kinematics which homing macro to run next
    /// </summary>
    /// <param name="toBeHomed">Axes still to home</param>
    /// <param name="axisLetters">Letter of each axis</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The macro, and the axes that must be homed before any of these can be</returns>
    private async ValueTask<(string FileName, uint MustHomeFirst)> NextHomingFileAsync(
        uint toBeHomed, char[] axisLetters, CancellationToken cancellationToken)
    {
        KinematicsEngine geometry = planner.Parameters.Geometry;
        uint alreadyHomed = await GetHomedAxesAsync(cancellationToken);

        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            // A machine with no Z endstop homes Z with its probe, and so does one whose Z endstop is
            // the probe. Both mean X and Y have to be homed first, which is what the kinematics needs
            // to know to answer at all
            Endstop? zEndstop = ZAxisIndex(model.Move) is int z && z >= 0 && z < model.Sensors.Endstops.Count
                                ? model.Sensors.Endstops[z]
                                : null;
            geometry.HomesZWithProbe = zEndstop is null || zEndstop.Type == EndstopType.ZProbeAsEndstop;
        }

        uint mustHomeFirst = geometry.GetHomingFileName(toBeHomed, alreadyHomed, axisLetters, out string fileName);
        return (fileName, mustHomeFirst);
    }

    /// <summary>
    /// Which axes are currently homed
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The axes, as a bitmap</returns>
    private async ValueTask<uint> GetHomedAxesAsync(CancellationToken cancellationToken)
    {
        uint homed = 0;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            for (int axis = 0; axis < model.Move.Axes.Count && axis < 32; axis++)
            {
                if (model.Move.Axes[axis].Homed)
                {
                    homed |= 1u << axis;
                }
            }
        }
        return homed;
    }

    /// <summary>
    /// Name a set of axes for a message
    /// </summary>
    /// <param name="axes">The axes, as a bitmap</param>
    /// <param name="axisLetters">Letter of each axis</param>
    /// <returns>The letters, in axis order</returns>
    private static string DescribeAxes(uint axes, char[] axisLetters)
    {
        StringBuilder builder = new();
        for (int axis = 0; axis < axisLetters.Length; axis++)
        {
            if ((axes & (1u << axis)) != 0)
            {
                builder.Append(axisLetters[axis]);
            }
        }
        return builder.ToString();
    }
}
