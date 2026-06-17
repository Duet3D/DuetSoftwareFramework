using System;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.IPC;
using Nito.AsyncEx;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.NotifyPluginStarted"/> command
/// </summary>
/// <param name="model">Object model</param>
public sealed class NotifyPluginStarted(Model.ObjectModel model) : DuetAPI.Commands.NotifyPluginStarted, IConnectionCommand
{
    /// <summary>
    /// Event that is set when a plugin has started
    /// </summary>
    public static readonly AsyncAutoResetEvent PluginStartedEvent = new(false);

    /// <inheritdoc />
    public Connection? Connection { get; set; }

    /// <summary>
    /// Flag the plugin as started
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // Fill in plugin name if required. Root-owned plugins skip the PID lookup in AssignPermissionsAsync, so ask
        // the matching plugin service to resolve our peer PID here
        if (string.IsNullOrEmpty(Plugin))
        {
            Plugin = await Connection!.ResolvePeerPluginIdAsync() ?? throw new UnauthorizedAccessException("Failed to determine plugin ID");
        }

        // Check permissions. Only the owner or plugins with the ManagePlugins permission may modify other plugins
        if (Connection!.PluginId != Plugin && !Connection!.Permissions.HasFlag(SbcPermissions.ManagePlugins))
        {
            throw new UnauthorizedAccessException("Insufficient permissions");
        }

        // Update the plugin state
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (model.Plugins.TryGetValue(Plugin, out Plugin? plugin))
            {
                plugin.Started = true;
                PluginStartedEvent.Set();
            }
            else
            {
                throw new ArgumentException($"Plugin {Plugin} not found");
            }
        }
    }
}
