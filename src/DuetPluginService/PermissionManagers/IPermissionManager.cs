
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;

namespace DuetPluginService.PermissionManagers;

public interface IPermissionManager
{
    /// <summary>
    /// Install the security profile for the given plugin
    /// </summary>
    /// <param name="plugin">Plugin</param>
    /// <param name="pluginDirectory">Plugin directory path</param>
    /// <param name="sdDirectory">SD directory path</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public Task InstallProfileAsync(Plugin plugin, string pluginDirectory, string sdDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// Uninstall the security profile for the given plugin
    /// </summary>
    /// <param name="plugin">Plugin</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public Task UninstallProfileAsync(Plugin plugin, CancellationToken cancellationToken);
}