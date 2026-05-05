using DuetAPI.Utility;

namespace DuetAPI.Commands;

/// <summary>
/// Command asking the plugin service which plugin owns a given process ID.
/// Used by DCS to close the TOCTOU window between process start and the
/// object model being updated with the new PID. Reserved for internal purposes, do not use
/// </summary>
[RequiredPermissions(SbcPermissions.ServicePlugins)]
public partial class ResolvePluginProcess : Command<string?>
{
    /// <summary>
    /// Process ID to look up. The plugin service resolves this by walking the
    /// process tree and matching ancestors against its own tracked plugin PIDs
    /// </summary>
    public int Pid { get; set; }
}
