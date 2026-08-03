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
    /// Adopt the position of every axis a homing move stopped at its endstop
    /// </summary>
    /// <param name="armedAxes">Axes the move was armed for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An awaitable task</returns>
    /// <remarks>
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
    private async ValueTask FinishHomingMoveAsync(IReadOnlyList<int> armedAxes, CancellationToken cancellationToken)
    {
        if (armedAxes.Count == 0)
        {
            return;
        }

        await planner.WaitForStandstillAsync(cancellationToken);

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

                    Axis axisConfig = model.Move.Axes[axis];
                    Endstop? endstop = axis < model.Sensors.Endstops.Count ? model.Sensors.Endstops[axis] : null;
                    if (endstop is null || !endstop.Triggered)
                    {
                        continue;               // the move ran its full length, so nothing is known
                    }

                    float position = endstop.HighEnd ? axisConfig.Max : axisConfig.Min;
                    planner.Builder.SetAxisPosition(axis, position);
                    axisConfig.MachinePosition = position;
                    axisConfig.UserPosition = position - WorkplaceOffset(axisConfig, model.Move.WorkplaceNumber);
                    axisConfig.Homed = true;
                }
            }
        }
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
    private async ValueTask<Message?> HandleHomeAsync(Commands.Code code, CancellationToken cancellationToken)
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
