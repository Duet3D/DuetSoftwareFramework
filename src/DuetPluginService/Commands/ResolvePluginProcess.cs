using DuetSharedLibrary;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DuetPluginService.IPC;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.ResolvePluginProcess"/> command
/// </summary>
/// <param name="pluginStore">Plugin store</param>
public sealed class ResolvePluginProcess(PluginStore pluginStore) : DuetAPI.Commands.ResolvePluginProcess
{
    /// <summary>
    /// Walk the process tree starting at <see cref="DuetAPI.Commands.ResolvePluginProcess.Pid"/>
    /// and return the id of the plugin that owns any ancestor. Used by DCS to close the
    /// window where a plugin has started but its PID has not yet propagated to the object model
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Plugin id, or null if no tracked plugin is an ancestor of the given PID</returns>
    public override async Task<string?> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        using (await pluginStore.LockAsync(cancellationToken))
        {
            for (int currentPid = Pid; currentPid > 1; currentPid = ProcessHelpers.GetParentPid(currentPid))
            {
                foreach ((string pluginId, Process process) in pluginStore.Processes)
                {
                    if (!process.HasExited && process.Id == currentPid)
                    {
                        return pluginId;
                    }
                }
            }
        }
        return null;
    }
}
