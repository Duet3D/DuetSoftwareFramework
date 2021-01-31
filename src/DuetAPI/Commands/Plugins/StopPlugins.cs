using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Stop all the plugins
    /// </summary>
    [RequiredPermissions(SbcPermissions.ManagePlugins)]
    public class StopPlugins : Command { }
}
