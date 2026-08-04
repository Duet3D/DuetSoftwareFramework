using System.Runtime.InteropServices;

namespace DuetControlServer.Link.Protocol.FirmwareRequests;

/// <summary>
/// Request part of a directory listing. This is followed by the directory name
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 12)]
public struct GetFileListHeader
{
    /// <summary>
    /// Index of the first entry to return
    /// </summary>
    public uint StartIndex;

    /// <summary>
    /// Maximum number of bytes of entry data the firmware can accept
    /// </summary>
    public uint MaxLength;

    /// <summary>
    /// Length of the directory name
    /// </summary>
    public ushort DirectoryLength;

    /// <summary>
    /// Padding
    /// </summary>
    public ushort Padding;
}
