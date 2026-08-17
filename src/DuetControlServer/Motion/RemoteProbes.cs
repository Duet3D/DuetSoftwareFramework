using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Motion.Native;

namespace DuetControlServer.Motion;

/// <summary>
/// How a Z probe configured by M558 is named on the CAN bus
/// </summary>
/// <remarks>
/// The same arrangement as <see cref="RemoteEndstops"/>, and for the same reason: M558 asks a board
/// to watch an input under a handle, and the receiver turns an incoming change back into a probe. The
/// handle is derived from the probe number rather than allocated, so neither side has to remember an
/// allocation
/// </remarks>
internal static class RemoteProbes
{
    /// <summary>
    /// How many probes may be configured, as in RepRapFirmware's <c>MaxZProbes</c>
    /// </summary>
    public const int MaxProbes = 4;

    /// <summary>
    /// Reading reported for a triggered digital probe
    /// </summary>
    /// <remarks>
    /// A digital probe has no reading of its own, but <c>sensors.probes[].value</c> is an analog
    /// scale and a client compares it against the threshold. The top of the scale is what
    /// RepRapFirmware reports for a closed digital probe
    /// </remarks>
    public const int MaxReading = 1000;

    /// <summary>
    /// The input handle a probe is monitored under
    /// </summary>
    /// <param name="probeNumber">Probe number</param>
    /// <returns>The handle</returns>
    /// <remarks>
    /// Major is the probe. Minor is unused: RepRapFirmware's <c>RemoteZProbe</c> leaves it zero too,
    /// because a probe is one input where an endstop may be one per driver
    /// </remarks>
    public static RemoteInputHandle HandleFor(int probeNumber)
    {
        RemoteInputHandle handle = default;
        handle.Type = (byte)RemoteInputHandle.TypeZprobe;
        handle.Major = (byte)probeNumber;
        handle.Minor = 0;
        return handle;
    }

    /// <summary>
    /// The board watching a probe's input, if anything is
    /// </summary>
    /// <param name="probe">The probe</param>
    /// <param name="board">CAN address of the board carrying its port</param>
    /// <returns>True if the probe has an input a board was asked to watch</returns>
    /// <remarks>
    /// A probe of type none is a placeholder for manual probing and a motor stall probe is detected
    /// by the driver, so neither has an input, and neither does one whose port has not been given
    /// yet. This is the same question <c>M558</c> asks before creating a monitor, so that nothing is
    /// sent to a handle that was never created
    /// </remarks>
    public static bool TryGetMonitoredBoard(Probe probe, out byte board)
    {
        board = 0;
        return probe.Type is not (ProbeType.None or ProbeType.ZMotorStall)
               && !string.IsNullOrWhiteSpace(probe.Port)
               && RemoteEndstops.TrySplitPort(probe.Port, "Z probe port", out board, out _, out _);
    }

    /// <summary>
    /// Fill in the switch a probing move should stop on
    /// </summary>
    /// <param name="probe">The probe</param>
    /// <param name="probeNumber">Probe number</param>
    /// <param name="action">
    /// What a trigger stops. RepRapFirmware has a class per use and they answer differently: a
    /// probing move is <c>ZProbe</c>, which stops every driver of the move, while the
    /// <c>ZProbeEndstop</c> standing in for an axis endstop stops the axis. Passed in rather than
    /// decided here, so that no caller can arm a drive without saying what a trigger on it stops
    /// </param>
    /// <param name="stopInput">Entry to fill in; left watching nothing if the probe cannot stop a move</param>
    /// <returns>True if the probe has an input a move can stop on</returns>
    /// <remarks>
    /// A probe of type none is a placeholder for manual probing and a motor stall probe is detected
    /// by the driver, so neither has an input a move can be armed on
    /// </remarks>
    public static bool TryGetStopInput(Probe probe, int probeNumber, StopAction action, MoveStopInput stopInput)
    {
        stopInput.Clear();
        if (!TryGetMonitoredBoard(probe, out byte board))
        {
            return false;
        }

        stopInput.SetShared(HandleFor(probeNumber), board);
        stopInput.Action = action;
        return true;
    }
}
