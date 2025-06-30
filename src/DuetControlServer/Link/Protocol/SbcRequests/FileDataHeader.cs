using System.Runtime.InteropServices;

namespace DuetControlServer.Link.Protocol.SbcRequests;

/// <summary>
/// Response to a file read request
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 4)]
public struct FileDataHeader
{
    /// <summary>
    /// Boolean value as byte
    /// </summary>
    public int BytesRead;
}
