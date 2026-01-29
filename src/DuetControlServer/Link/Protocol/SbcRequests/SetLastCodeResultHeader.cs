using DuetAPI;
using DuetControlServer.Link.Protocol.Shared;
using System.Runtime.InteropServices;

namespace DuetControlServer.Link.Protocol.SbcRequests;

/// <summary>
/// Header to set the last result of a code executed by the SBC
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 4)]
public struct SetLastCodeResultHeader
{
    /// <summary>
    /// Channel of the code
    /// </summary>
    public CodeChannel Channel;

    /// <summary>
    /// Result of the code execution
    /// </summary>
    public CodeResult Result;
}
