using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Invalidate all pending codes and files on a given channel (including buffered codes from DSF in RepRapFirmware)
    /// </summary>
    [RequiredPermissions(SbcPermissions.CodeInterceptionReadWrite)]
    public class InvalidateChannel : Command
    {
        /// <summary>
        /// Code channel to invalidate
        /// </summary>
        public CodeChannel Channel { get; set; }
    }
}
