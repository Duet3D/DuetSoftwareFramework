using System.Runtime.InteropServices;

namespace DuetControlServer.Link.Protocol.SbcRequests;

/// <summary>
/// Configure a CAN bus interface
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 4)]
public struct ConfigCanHeader
{
    /// <summary>
    /// channel number (0 or 1)
    /// </summary>
    public byte Channel;

    /// <summary>
    /// true if the CAN bus interface should use the FD protocol
    /// </summary>
    public byte UseFd;

    /// <summary>
    /// Data rate multiplier for the CAN bus interface
    /// </summary>
    public byte DataRateMultiplier;
}
