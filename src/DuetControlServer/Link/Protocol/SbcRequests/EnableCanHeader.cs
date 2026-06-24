using System.Runtime.InteropServices;

namespace DuetControlServer.Link.Protocol.SbcRequests;

/// <summary>
/// Enable or disable a CAN bus interface
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 4)]
public struct EnableCanHeader
{
    /// <summary>
    /// channel number (0 or 1)
    /// </summary>
    public byte Channel;

    /// <summary>
    /// true if the CAN bus interface should be enabled
    /// </summary>
    public byte Enable;
}
