using System;
using System.Threading;
using System.Threading.Tasks;
using DuetControlServer.IPC.Processors;
using Microsoft.Extensions.Options;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.UninstallSystemPackage"/> command
/// </summary>
/// <param name="settings">Settings</param>
public sealed class UninstallSystemPackage(IOptions<Settings> settings) : DuetAPI.Commands.UninstallSystemPackage
{
    /// <summary>
    /// Uninstall a system package
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="ArgumentException">Package could not be uninstalled</exception>
    public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (!settings.Value.RootPluginSupport)
        {
            throw new NotSupportedException("Root plugin support has been disabled");
        }

        // Forward this command to the plugin services
        await PluginService.PerformCommandAsync(this, true, cancellationToken);
    }
}
