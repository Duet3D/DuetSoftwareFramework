using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Command to update the process identifier of a given plugin.
    /// Reserved for internal purposes, do not use
    /// </summary>
    [RequiredPermissions(SbcPermissions.ServicePlugins)]
    public class SetPluginProcess : Command
    {
        /// <summary>
        /// Identifier of the plugin to update
        /// </summary>
        public string Plugin { get; set; } = string.Empty;

        /// <summary>
        /// New process identifier of the plugin
        /// </summary>
        public int Pid { get; set; }
    }
}
