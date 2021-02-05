using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Start all the previously started plugins again
    /// </summary>
    [RequiredPermissions(SbcPermissions.ManagePlugins)]
    public class StartPlugins : Command { }
}
