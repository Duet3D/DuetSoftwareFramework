using DuetAPI.ObjectModel;
using DuetAPI.Utility;

namespace DuetAPI.Commands;

/// <summary>
/// Flag a given network protocol as enabled or disabled
/// </summary>
[RequiredPermissions(SbcPermissions.SuperUser)]
public partial class SetNetworkProtocol : Command
{
    /// <summary>
    /// Protocol to change
    /// </summary>
    public NetworkProtocol Protocol { get; set; }

    /// <summary>
    /// Whether the protocol is enabled or not
    /// </summary>
    public bool Enabled { get; set; }
}
