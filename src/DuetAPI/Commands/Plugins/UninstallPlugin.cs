using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Uninstall a plugin
    /// </summary>
    [RequiredPermissions(SbcPermissions.ManagePlugins)]
    public class UninstallPlugin : Command
    {
        /// <summary>
        /// Identifier of the plugin
        /// </summary>
        public string Plugin { get; set; }
    }
}
