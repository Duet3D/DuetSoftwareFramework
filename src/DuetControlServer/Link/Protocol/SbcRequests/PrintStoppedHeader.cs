using DuetControlServer.Link.Protocol.Shared;
using System.Runtime.InteropServices;

namespace DuetControlServer.Link.Protocol.SbcRequests;

/// <summary>
/// Header of print stop notifications
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 4)]
public struct PrintStoppedHeader
{
    /// <summary>
    /// Reason why the print has been stopped
    /// </summary>
    public PrintStoppedReason Reason;
}
