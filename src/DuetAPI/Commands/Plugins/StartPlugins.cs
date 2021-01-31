using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Start all the plugins
    /// </summary>
    [RequiredPermissions(SbcPermissions.ManagePlugins)]
    public class StartPlugins : Command { }
}
