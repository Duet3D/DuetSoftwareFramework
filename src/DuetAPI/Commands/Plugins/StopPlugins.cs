using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Stop all the plugins and save which plugins were started before.
    /// This command is intended for shutdown or update requests
    /// </summary>
    [RequiredPermissions(SbcPermissions.ManagePlugins)]
    public class StopPlugins : Command { }
}
