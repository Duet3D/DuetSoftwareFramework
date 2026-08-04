using System.Runtime.InteropServices;

namespace DuetControlServer.Link.Protocol.SbcRequests;

/// <summary>
/// Response to a <see cref="FirmwareRequests.GetFileListHeader"/>.
/// This is followed by DataLength bytes of <see cref="FileListEntry"/> records
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 8)]
public struct FileListHeader
{
    /// <summary>
    /// Number of bytes of entry data following this header
    /// </summary>
    public uint DataLength;

    /// <summary>
    /// Whether the final entry of the directory is part of this response
    /// </summary>
    public byte EndOfList;

    /// <summary>
    /// Padding
    /// </summary>
    public byte PaddingA;

    /// <summary>
    /// Padding
    /// </summary>
    public ushort PaddingB;
}
