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
/// Dropping an input monitor a board was asked to keep and is no longer wanted
/// </summary>
/// <remarks>
/// <para>
/// A board asked to watch a pin holds it until it is told otherwise: it goes on reporting an input
/// nobody reads, and it keeps the pin claimed, so naming that pin in a later <c>M950</c> fails
/// because it is already in use.
/// </para>
/// <para>
/// Reconfiguring an endstop or a probe to a different pin needs nothing from here, because creating a
/// monitor replaces any monitor already under the same handle. What needs deleting is a handle that
/// is <em>abandoned</em> - an endstop removed, changed to a stall, or given fewer switches than it
/// had, and a probe set to type none
/// </para>
/// </remarks>
internal static class InputMonitors
{
    /// <summary>
    /// One monitor a board was asked to keep
    /// </summary>
    /// <param name="Board">CAN address of the board holding it</param>
    /// <param name="Handle">Handle it was created under</param>
    /// <remarks>
    /// Captured before the object model is overwritten, because what has to be deleted is what the
    /// <em>previous</em> configuration created, and the board is the one the old port named - which
    /// need not be the one the new port names
    /// </remarks>
    public readonly record struct Monitored(byte Board, RemoteInputHandle Handle);

    /// <summary>
    /// The monitors an endstop has asked boards to keep
    /// </summary>
    /// <param name="endstop">The endstop, or null if the axis has none</param>
    /// <param name="axis">Axis it belongs to</param>
    /// <returns>Its monitors, empty if it has none</returns>
    /// <remarks>
    /// The caller must hold the object model lock. Only a switch on a pin is monitored: a stall is
    /// detected by the driver and a Z probe endstop is watched under the probe's own handle
    /// </remarks>
    public static List<Monitored> Of(Endstop? endstop, int axis)
    {
        List<Monitored> monitors = [];
        if (endstop is null || endstop.Type != EndstopType.InputPin)
        {
            return monitors;
        }

        string[] ports = RemoteEndstops.PortsOf(endstop);
        for (int switchIndex = 0; switchIndex < ports.Length; switchIndex++)
        {
            if (RemoteEndstops.TrySplitPort(ports[switchIndex], "Endstop port", out byte board, out _, out _))
            {
                monitors.Add(new Monitored(board, RemoteEndstops.HandleFor(axis, switchIndex)));
            }
        }
        return monitors;
    }

    /// <summary>
    /// The monitor a probe has asked a board to keep
    /// </summary>
    /// <param name="probe">The probe, or null if there is none</param>
    /// <param name="probeNumber">Probe number</param>
    /// <returns>Its monitor, empty if it has none</returns>
    /// <remarks>The caller must hold the object model lock</remarks>
    public static List<Monitored> Of(Probe? probe, int probeNumber)
    {
        List<Monitored> monitors = [];
        if (probe is not null && RemoteProbes.TryGetMonitoredBoard(probe, out byte board))
        {
            monitors.Add(new Monitored(board, RemoteProbes.HandleFor(probeNumber)));
        }
        return monitors;
    }

    /// <summary>
    /// Whether a monitor is one the new configuration will ask for again
    /// </summary>
    /// <param name="monitor">The monitor</param>
    /// <param name="wanted">What the new configuration will create</param>
    /// <returns>True if it is wanted</returns>
    /// <remarks>
    /// Compared on the handle's value rather than on the handle itself, because a
    /// <see cref="RemoteInputHandle"/> is a union of the whole and its fields and only the whole is
    /// meaningful to compare
    /// </remarks>
    private static bool IsKept(Monitored monitor, IReadOnlyList<Monitored> wanted)
    {
        foreach (Monitored other in wanted)
        {
            if (other.Board == monitor.Board && other.Handle.All == monitor.Handle.All)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Drop the monitors that were kept before and are not wanted now
    /// </summary>
    /// <param name="before">What the previous configuration created</param>
    /// <param name="after">What the new one will create</param>
    /// <param name="link">Link interface</param>
    /// <param name="logger">Logger</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An awaitable task</returns>
    /// <remarks>
    /// <para>
    /// A monitor that appears in both is left alone rather than deleted and re-created. Creating
    /// replaces it anyway, and deleting first would mean a create that failed left the axis with no
    /// monitor where it had a working one.
    /// </para>
    /// <para>
    /// Nothing here fails the code that asked for it. The reason to delete is to release a pin for
    /// something later to claim, and turning a tidy-up into a configuration error would refuse an
    /// <c>M574</c> that is otherwise perfectly good. A board that will not let go of a pin is worth
    /// a warning, because the next <c>M950</c> is where it will be felt
    /// </para>
    /// </remarks>
    public static async ValueTask ReleaseAsync(IReadOnlyList<Monitored> before, IReadOnlyList<Monitored> after,
                                               LinkInterface link, ILogger logger, CancellationToken cancellationToken)
    {
        foreach (Monitored monitor in before)
        {
            if (IsKept(monitor, after))
            {
                continue;
            }

            CanMessageChangeInputMonitorV1 message = new()
            {
                Handle = monitor.Handle,
                Param = 0,
                Action = CanMessageChangeInputMonitorV1.ActionDelete
            };

            try
            {
                // A board that does not have the handle answers without complaint, which is what
                // makes this safe to send from what DSF believes rather than from what the board
                // confirmed: a board that restarted since has already forgotten it
                CanResponse response = await link.SendCanMessageAsync(monitor.Board, in message,
                                                                      CanMessageType.StandardReply,
                                                                      cancellationToken: cancellationToken);
                if (response.Severity != MessageType.Success)
                {
                    logger.LogWarning("Board {Board} did not release the pin behind handle {Handle}: {Reply}",
                                      monitor.Board, monitor.Handle.All, response.ToMessage().Content);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Could not drop the input monitor behind handle {Handle} on board {Board}",
                                  monitor.Handle.All, monitor.Board);
            }
        }
    }
}
