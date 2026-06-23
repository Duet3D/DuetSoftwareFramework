using System.Runtime.InteropServices;

namespace DuetControlServer.Link.Protocol.SbcRequests;

/// <summary>
/// Configure the CAN bus interface
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 4)]
public struct EnableCANHeader
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
