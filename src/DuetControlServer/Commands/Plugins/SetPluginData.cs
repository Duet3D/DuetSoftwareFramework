using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.IPC;
using Microsoft.Extensions.Options;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.SetPluginData"/> command
/// </summary>
/// <param name="model">Object model</param>
/// <param name="settings">Settings</param>
public sealed class SetPluginData(Model.ObjectModel model, IOptions<Settings> settings) : DuetAPI.Commands.SetPluginData, IConnectionCommand
{
    /// <inheritdoc />
    public Connection? Connection { get; set; }

    /// <summary>
    /// Set custom plugin data in the object model
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (!settings.Value.PluginSupport)
        {
            throw new NotSupportedException("Plugin support has been disabled");
        }

        // Fill in plugin name if required. Root-owned plugins skip the PID lookup in AssignPermissionsAsync, so ask
        // the matching plugin service to resolve our peer PID here
        if (string.IsNullOrEmpty(Plugin))
        {
            Plugin = await Connection!.ResolvePeerPluginIdAsync() ?? throw new UnauthorizedAccessException("Failed to determine plugin ID");
        }

        // Check permissions. Only the owner or plugins with the ManagePlugins permission may modify plugin data
        if (Connection!.PluginId != Plugin && !Connection!.Permissions.HasFlag(SbcPermissions.ManagePlugins))
        {
            throw new UnauthorizedAccessException("Insufficient permissions");
        }

        // Update the plugin data
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (model.Plugins.TryGetValue(Plugin, out Plugin? plugin))
            {
                if (!plugin.Data.ContainsKey(Key))
                {
                    throw new ArgumentException($"Key {Key} not found in the plugin data");
                }
                plugin.Data[Key] = Value.Clone();        // create a clone so that the instance can be used even after the JsonDocument is disposed
            }
            else
            {
                throw new ArgumentException($"Plugin {Plugin} not found");
            }
        }
    }
}
