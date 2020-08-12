using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Wait for the machine model to be fully updated from RepRapFirmware
    /// </summary>
    [RequiredPermissions(SbcPermissions.CommandExecution | SbcPermissions.ObjectModelRead | SbcPermissions.ObjectModelReadWrite)]
    public class SyncObjectModel : Command { }
}
