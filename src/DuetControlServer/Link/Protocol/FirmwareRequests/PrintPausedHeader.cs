using System.Runtime.InteropServices;

namespace DuetControlServer.Link.Protocol.FirmwareRequests;

/// <summary>
/// Header of print pause events
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 8)]
public struct LegacyPrintPausedHeader
{
    /// <summary>
    /// Position at which the file has been paused
    /// </summary>
    public uint FilePosition;

    /// <summary>
    /// Reason why the print has been paused
    /// </summary>
    /// <seealso cref="PrintPausedReason"/>
    public byte PauseReason;
}

/// <summary>
/// Header of print pause events
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 12)]
public struct PrintPausedHeader
{
    /// <summary>
    /// Position at which the file has been paused
    /// </summary>
    public uint FilePosition;

    /// <summary>
    /// Position at which the second open file has been paused (if applicable)
    /// </summary>
    public uint FilePosition2;

    /// <summary>
    /// Reason why the print has been paused
    /// </summary>
    /// <seealso cref="PrintPausedReason"/>
    public byte PauseReason;
}
