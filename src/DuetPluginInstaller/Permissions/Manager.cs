using DuetAPI.ObjectModel;
using System.Threading.Tasks;

namespace DuetPluginService.Permissions
{
    /// <summary>
    /// Functions to manage security profiles for third-party plugins
    /// </summary>
    public static class Manager
    {
        /// <summary>
        /// Install a security profile for a given plugin
        /// </summary>
        /// <param name="plugin">Plugin</param>
        /// <returns>Asynchronous task</returns>
        public static Task InstallProfile(Plugin plugin) => AppArmor.InstallProfile(plugin);

        /// <summary>
        /// Uninstall a security profile for a given plugin
        /// </summary>
        /// <param name="plugin">Plugin</param>
        /// <returns>Asynchronous task</returns>
        public static Task UninstallProfile(Plugin plugin) => AppArmor.UninstallProfile(plugin);
    }
}
