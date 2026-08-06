using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Motion;
using DuetControlServer.Motion.Native;
using Microsoft.Extensions.Logging;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// Arming and disarming the endstops that are not inputs on a pin
/// </summary>
/// <remarks>
/// <para>
/// A switch and a Z probe are watched by whichever board carries the pin, and M574 and M558 have
/// already asked for that, so a move only has to name the handle. A stall endstop is different: the
/// driver decides it has stalled, and it can only do that if it has been told what speed to expect,
/// because the whole method is comparing the back-EMF against what the commanded speed implies. So
/// the drivers have to be armed for this particular move before it runs, and disarmed afterwards.
/// </para>
/// <para>
/// That arming is a CAN round trip per driver, which is why it happens here rather than in
/// <c>ApplyEndstops</c>: the object model lock must not be held across it. The speeds are worked out
/// from the code alone for the same reason. RepRapFirmware's own calculation is explicitly an
/// approximation - it says so, and notes that it duplicates <c>DDA::InitStandardMove</c> - because
/// all the driver needs is the order of magnitude it should expect
/// </para>
/// </remarks>
internal sealed partial class GCodeHandler
{
    /// <summary>
    /// One driver that has to be told what to expect before a stall-homing move
    /// </summary>
    /// <param name="Driver">The driver</param>
    /// <param name="StepsPerSecond">Speed it will turn at, which is what sets the stall threshold</param>
    private readonly record struct StallArming(DuetAPI.Utility.DriverId Driver, float StepsPerSecond);

    /// <summary>
    /// Tell every driver of a stall-homed axis what speed to expect
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The boards that were armed and what they said about it, if anything</returns>
    /// <remarks>
    /// Ported from <c>StallDetectionEndstop::PrimeAxis</c> by way of
    /// <c>CanInterface::EnableRemoteStallEndstop</c>. Nothing happens for a move whose axes are all
    /// homed by switches, which is the common case
    /// </remarks>
    private async ValueTask<(HashSet<byte> Boards, Message Reply)> ArmStallEndstopsAsync(
        Commands.Code code, CancellationToken cancellationToken)
    {
        List<StallArming> toArm = [];
        HashSet<byte> boards = [];
        List<Message> replies = [];

        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            int numAxes = Math.Min(planner.Parameters.NumAxes, model.Move.Axes.Count);
            float feedRateMmPerSec = StallHomingSpeed(code, numAxes);

            for (int axis = 0; axis < numAxes; axis++)
            {
                if (!code.HasParameter(model.Move.Axes[axis].Letter))
                {
                    continue;
                }

                Endstop? endstop = axis < model.Sensors.Endstops.Count ? model.Sensors.Endstops[axis] : null;
                if (endstop is null || endstop.Type is not (EndstopType.MotorStallAny or EndstopType.MotorStallIndividual))
                {
                    continue;
                }

                // Every drive that has to turn for this axis to move, which on coupled kinematics is
                // more than the axis' own
                uint drives = planner.Parameters.Geometry.GetControllingDrives(axis);
                for (int drive = 0; drive < numAxes && drive < MotionLimits.MaxAxesPlusExtruders; drive++)
                {
                    if ((drives & (1u << drive)) == 0)
                    {
                        continue;
                    }

                    // The driver is told steps per second, because that is what it compares against
                    float stepsPerSecond = MathF.Abs(feedRateMmPerSec * planner.Parameters.StepsPerMm[drive]);
                    foreach (DuetAPI.Utility.DriverId driver in model.Move.Axes[drive].Drivers)
                    {
                        toArm.Add(new StallArming(driver, stepsPerSecond));
                    }
                }
            }
        }

        foreach (StallArming arming in toArm)
        {
            CanMessageEnableStallEndstop message = new()
            {
                DriverNumber = (ushort)arming.Driver.Port,
                Speed = arming.StepsPerSecond
            };

            byte board = (byte)arming.Driver.Board;
            CanResponse response = await linkInterface.SendCanMessageAsync(
                board, in message, CanMessageType.StandardReply, cancellationToken: cancellationToken);
            boards.Add(board);

            Message reply = response.ToMessage();
            if (reply.Type == MessageType.Error)
            {
                // Some drivers may already be armed, so the caller still has to disarm what it got
                return (boards, reply);
            }

            // The driver was armed but the board may still have had something to say about it, which
            // the move carries back rather than dropping: a warning here is the only sign the user
            // gets that the stall threshold may not be what they asked for
            replies.Add(reply);
        }
        return (boards, replies.ToMessage());
    }

    /// <summary>
    /// Stop every board watching for stalls
    /// </summary>
    /// <param name="boards">Boards that were armed</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An awaitable task</returns>
    /// <remarks>
    /// One message disables every stall endstop on a board, as in
    /// <c>CanInterface::DisableRemoteStallEndstops</c>. It has to happen however the move ended: a
    /// driver left armed would report a stall during an ordinary move, and the next move that named
    /// the stall handle would stop on it
    /// </remarks>
    private async ValueTask DisarmStallEndstopsAsync(IEnumerable<byte> boards, CancellationToken cancellationToken)
    {
        foreach (byte board in boards)
        {
            CanMessageEnableStallEndstop message = new()
            {
                DriverNumber = CanMessageEnableStallEndstop.DisableAll,
                Speed = 0.0f
            };

            try
            {
                CanResponse response = await linkInterface.SendCanMessageAsync(board, in message, CanMessageType.StandardReply,
                                                                               cancellationToken: cancellationToken);

                // The move this cleans up after has already run, so there is nobody left to answer;
                // a board that would not disarm is still worth a line in the log, because the next
                // move naming the stall handle is what will notice
                if (response.Severity != MessageType.Success)
                {
                    logger.LogWarning("Board {Board} did not disable its stall endstops: {Reply}", board, response.ToMessage().Content);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Worth knowing about but not worth failing the move that has already run for
                logger.LogWarning(e, "Could not disable the stall endstops on board {Board}", board);
            }
        }
    }

    /// <summary>
    /// About how fast a stall-homing move will run
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
    private float StallHomingSpeed(Commands.Code code, int numAxes)
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
