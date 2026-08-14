using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.Logging;

namespace DuetControlServer.Motion;

/// <summary>
/// Telling drivers to report a stall for the duration of one move, and untelling them afterwards
/// </summary>
/// <remarks>
/// <para>
/// A driver decides it has stalled by comparing the back-EMF against what the commanded speed
/// implies, so it cannot detect one until it has been told what speed to expect. That makes arming
/// per move, and a CAN round trip per driver, which is why it happens before the move is built rather
/// than while it is being built.
/// </para>
/// <para>
/// Two things arm drivers this way and they are not both endstops: an axis homed on a stall, and a
/// <c>Z</c> probe of type <c>M558 P10</c>. Sharing one implementation is what keeps a driver armed
/// for a probing move from being armed differently from one armed for a homing move
/// </para>
/// </remarks>
internal static class StallArming
{
    /// <summary>
    /// Tell each driver what speed to expect, so that it can report a stall
    /// </summary>
    /// <param name="drivers">Drivers to arm, and how fast each will turn</param>
    /// <param name="state">Records the boards armed, so that they can be released</param>
    /// <param name="link">Link interface</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Anything the boards had to say that is worth passing on</returns>
    /// <exception cref="GCodeException">A board refused, so the move must not run</exception>
    public static async ValueTask<Message> ArmAsync(IReadOnlyList<WatchedDriver> drivers, EndstopArmingState state,
                                                    LinkInterface link, CancellationToken cancellationToken)
    {
        List<Message> replies = [];
        foreach (WatchedDriver watched in drivers)
        {
            CanMessageEnableStallEndstop message = new()
            {
                DriverNumber = (ushort)watched.Driver.Port,
                Speed = watched.StepsPerSecond
            };

            byte board = (byte)watched.Driver.Board;
            CanResponse response = await link.SendCanMessageAsync(
                board, in message, CanMessageType.StandardReply, cancellationToken: cancellationToken);

            // Recorded before the reply is judged: a board that refused one driver may already have
            // armed another, and the release has to reach it either way
            state.ArmedBoards.Add(board);

            Message reply = response.ToMessage();
            if (reply.Type == MessageType.Error)
            {
                throw new GCodeException(reply.Content);
            }

            // The driver was armed but the board may still have had something to say about it, which
            // the caller carries back rather than dropping: a warning here is the only sign the user
            // gets that the stall threshold may not be what they asked for
            replies.Add(reply);
        }
        return replies.ToMessage();
    }

    /// <summary>
    /// Stop every armed board watching for stalls
    /// </summary>
    /// <param name="state">What was armed</param>
    /// <param name="link">Link interface</param>
    /// <param name="logger">Logger, because the move this cleans up after has already run</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An awaitable task</returns>
    /// <remarks>
    /// One message disables every stall endstop on a board. It has to happen however the move ended:
    /// a driver left armed reports a stall during an ordinary move, and the next move that named the
    /// stall handle would stop on it
    /// </remarks>
    public static async ValueTask ReleaseAsync(EndstopArmingState state, LinkInterface link, ILogger logger,
                                               CancellationToken cancellationToken)
    {
        foreach (byte board in state.ArmedBoards)
        {
            CanMessageEnableStallEndstop message = new()
            {
                DriverNumber = CanMessageEnableStallEndstop.DisableAll,
                Speed = 0.0f
            };

            try
            {
                CanResponse response = await link.SendCanMessageAsync(board, in message, CanMessageType.StandardReply,
                                                                      cancellationToken: cancellationToken);

                // There is nobody left to answer; a board that would not disarm is still worth a line
                // in the log, because the next move naming the stall handle is what will notice
                if (response.Severity != MessageType.Success)
                {
                    logger.LogWarning("Board {Board} did not disable its stall endstops: {Reply}", board,
                                      response.ToMessage().Content);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Worth knowing about but not worth failing the move that has already run for
                logger.LogWarning(e, "Could not disable the stall endstops on board {Board}", board);
            }
        }
        state.ArmedBoards.Clear();
    }
}
