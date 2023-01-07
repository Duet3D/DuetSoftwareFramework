using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Reload the manifest of a given plugin. Useful for packaged plugins
    /// </summary>
    [RequiredPermissions(SbcPermissions.ManagePlugins)]
    public class ReloadPlugin : Command
    {
        /// <summary>
        /// Identifier of the plugin
        /// </summary>
        public string Plugin { get; set; } = string.Empty;
    }
}
