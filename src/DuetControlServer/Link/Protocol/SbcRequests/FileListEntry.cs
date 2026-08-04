using System.Runtime.InteropServices;

namespace DuetControlServer.Link.Protocol.SbcRequests;

/// <summary>
/// One entry of a <see cref="FileListHeader"/> response.
/// This is followed by the name padded to the next dword boundary
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 12)]
public struct FileListEntry
{
    /// <summary>
    /// Size of the file in bytes, zero for directories
    /// </summary>
    public uint Size;

    /// <summary>
    /// Time of the last modification in seconds since 1 Jan 1970, zero if unknown
    /// </summary>
    public uint LastModified;

    /// <summary>
    /// Length of the name
    /// </summary>
    public ushort NameLength;

    /// <summary>
    /// Whether this entry is a directory
    /// </summary>
    public byte IsDirectory;

    /// <summary>
    /// Padding
    /// </summary>
    public byte Padding;
}
