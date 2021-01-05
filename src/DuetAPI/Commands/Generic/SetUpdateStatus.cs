using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Override the current status as reported by the object model when performing a software update
    /// </summary>
    [RequiredPermissions(SbcPermissions.ObjectModelReadWrite)]
    public class SetUpdateStatus : Command
    {
        /// <summary>
        /// Whether an update is now in progress
        /// </summary>
        public bool Updating { get; set; }
    }
}
