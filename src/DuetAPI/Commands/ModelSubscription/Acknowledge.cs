using DuetAPI.Connection;
using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Acknowledge a (partial) model update.
    /// </summary>
    /// <remarks>
    /// This command is only permitted in <see cref="ConnectionMode.Subscribe"/> mode
    /// </remarks>
    [RequiredPermissions(SbcPermissions.ObjectModelRead | SbcPermissions.ObjectModelReadWrite)]
    public class Acknowledge : Command { }
}
