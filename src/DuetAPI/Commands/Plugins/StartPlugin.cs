using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Start a plugin
    /// </summary>
    [RequiredPermissions(SbcPermissions.ManagePlugins)]
    public class StartPlugin : Command
    {
        /// <summary>
        /// Name of the plugin
        /// </summary>
        public string Plugin { get; set; }
    }
}
