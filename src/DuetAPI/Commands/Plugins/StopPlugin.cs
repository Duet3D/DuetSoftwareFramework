using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Stop a plugin
    /// </summary>
    [RequiredPermissions(SbcPermissions.ManagePlugins)]
    public class StopPlugin : Command
    {
        /// <summary>
        /// Name of the plugin
        /// </summary>
        public string Plugin { get; set; }
    }
}
