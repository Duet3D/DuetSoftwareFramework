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
        public string Plugin { get; set; } = string.Empty;

        /// <summary>
        /// Indicates that the plugin is being uninstalled for an upgrade, so only the files it installed are removed.
        /// Reserved for internal purposes, do not use
        /// </summary>
        public bool ForUpgrade { get; set; }
    }
}
