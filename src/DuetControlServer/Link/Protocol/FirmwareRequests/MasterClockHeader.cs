using System.Runtime.InteropServices;

namespace DuetControlServer.Link.Protocol.FirmwareRequests;

/// <summary>
/// Master clock used for motion control
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 8)]
public struct MasterClockHeader
{
    /// <summary>
    /// Last master clock in milliseconds
    /// </summary>
    public uint MasterClock;

    /// <summary>
    /// Hiccup time in milliseconds
    /// </summary>
    public uint HiccupTime;
}
