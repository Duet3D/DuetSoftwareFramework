using System;
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
/// Telling a probe's board that probing is starting, and telling it when it has stopped
/// </summary>
/// <remarks>
/// <para>
/// RepRapFirmware's <c>RemoteZProbe::SetProbing</c>. Two things are pushed to the board around a tap
/// rather than at configuration time, and both have to be, for different reasons.
/// </para>
/// <para>
/// The threshold, because <c>G31 P</c> may have changed it since <c>M558</c> ran. Reading it here
/// means the board is given whatever last wrote it, so there is no <c>G31</c>-side message and no way
/// for the two to disagree - which they would otherwise, since the board decides when to report and
/// DCS decides what the report means.
/// </para>
/// <para>
/// The reporting interval, because a probe is only worth listening to closely while it is being used.
/// An analog probe near its threshold changes reading constantly, and every change is a CAN message
/// on a bus a move is already sharing
/// </para>
/// </remarks>
internal static class ProbeArming
{
    /// <summary>
    /// What has to be known about a probe to tell its board about a tap
    /// </summary>
    /// <param name="ProbeNumber">Probe number, which is what its handle is derived from</param>
    /// <param name="Board">CAN address of the board watching its input</param>
    /// <param name="Threshold">Trigger level, or null if the probe is not one that compares against one</param>
    /// <remarks>
    /// Captured under the object model lock and used outside it, because sending is a CAN round trip.
    /// A live <see cref="Probe"/> read outside the lock could be reconfigured half way through
    /// </remarks>
    public readonly record struct ProbeMonitor(int ProbeNumber, byte Board, uint? Threshold);

    /// <summary>
    /// Take what is needed to arm a probe, if it has an input at all
    /// </summary>
    /// <param name="probe">The probe</param>
    /// <param name="probeNumber">Probe number</param>
    /// <param name="monitor">What to send to</param>
    /// <returns>True if the probe has a monitor to tell</returns>
    /// <remarks>The caller must hold the object model lock</remarks>
    public static bool TryCapture(Probe probe, int probeNumber, out ProbeMonitor monitor)
    {
        if (!RemoteProbes.TryGetMonitoredBoard(probe, out byte board))
        {
            monitor = default;
            return false;
        }

        // Only an analog probe has a threshold to compare against. A digital probe was created with
        // zero, which is what tells the board to read the pin digitally, and sending it a nonzero
        // threshold would switch it to analog reads and stop it reporting at all
        uint? threshold = probe.Type is ProbeType.Analog or ProbeType.ScanningAnalog
                          ? (uint)Math.Max(probe.Threshold, 0)
                          : null;
        monitor = new ProbeMonitor(probeNumber, board, threshold);
        return true;
    }

    /// <summary>
    /// Shortest interval in ms between a probe reporting twice while it is probing
    /// </summary>
    /// <remarks>RepRapFirmware's <c>RemoteZProbe::ActiveProbeReportInterval</c></remarks>
    public const uint ActiveReportInterval = 2;

    /// <summary>
    /// Shortest interval in ms between a probe reporting twice at any other time
    /// </summary>
    /// <remarks>RepRapFirmware's <c>RemoteZProbe::InactiveProbeReportInterval</c></remarks>
    public const uint InactiveReportInterval = 25;

    /// <summary>
    /// Tell a probe's board that probing is about to start
    /// </summary>
    /// <param name="monitor">What to tell, from <see cref="TryCapture"/></param>
    /// <param name="link">Link interface</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Anything the board had to say that is worth passing on</returns>
    /// <exception cref="GCodeException">The board refused, so the tap must not run</exception>
    public static async ValueTask<Message> StartAsync(ProbeMonitor monitor, LinkInterface link,
                                                      CancellationToken cancellationToken)
    {
        if (monitor.Threshold is uint threshold)
        {
            Message reply = await ChangeAsync(monitor, CanMessageChangeInputMonitorV1.ActionChangeThreshold,
                                              threshold, link, cancellationToken);
            if (reply.Type == MessageType.Error)
            {
                throw new GCodeException(reply.Content);
            }
        }

        Message intervalReply = await ChangeAsync(monitor, CanMessageChangeInputMonitorV1.ActionChangeMinInterval,
                                                  ActiveReportInterval, link, cancellationToken);
        if (intervalReply.Type == MessageType.Error)
        {
            throw new GCodeException(intervalReply.Content);
        }
        return intervalReply;
    }

    /// <summary>
    /// Put a probe's board back to reporting at the idle rate, however the tap ended
    /// </summary>
    /// <param name="monitor">What to tell, from <see cref="TryCapture"/></param>
    /// <param name="link">Link interface</param>
    /// <param name="logger">Logger, because the tap this cleans up after has already run</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An awaitable task</returns>
    /// <remarks>
    /// A probe left at the probing rate is what DSF did before this existed, so failing here costs
    /// bus traffic rather than correctness - which is why it is logged and not thrown
    /// </remarks>
    public static async ValueTask StopAsync(ProbeMonitor monitor, LinkInterface link, ILogger logger,
                                            CancellationToken cancellationToken)
    {
        try
        {
            Message reply = await ChangeAsync(monitor, CanMessageChangeInputMonitorV1.ActionChangeMinInterval,
                                              InactiveReportInterval, link, cancellationToken);
            if (reply.Type == MessageType.Error)
            {
                logger.LogWarning("Probe {Probe} was left reporting at the probing rate: {Reply}",
                                  monitor.ProbeNumber, reply.Content);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not slow probe {Probe} back down", monitor.ProbeNumber);
        }
    }

    /// <summary>
    /// Send one change to a probe's input monitor
    /// </summary>
    /// <param name="monitor">Which probe's monitor to change</param>
    /// <param name="action">What to change</param>
    /// <param name="param">The new value</param>
    /// <param name="link">Link interface</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What the board said</returns>
    private static async ValueTask<Message> ChangeAsync(ProbeMonitor monitor, byte action, uint param,
                                                        LinkInterface link, CancellationToken cancellationToken)
    {
        CanMessageChangeInputMonitorV1 message = new()
        {
            Handle = RemoteProbes.HandleFor(monitor.ProbeNumber),
            Param = param,
            Action = action
        };

        CanResponse response = await link.SendCanMessageAsync(monitor.Board, in message, CanMessageType.StandardReply,
                                                              cancellationToken: cancellationToken);
        return response.ToMessage();
    }
}
