using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Check if the given password is correct and matches the previously set value from M551.
    /// If no password was configured before or if it was set to "reprap", this will always return true
    /// </summary>
    [RequiredPermissions(SbcPermissions.ObjectModelRead | SbcPermissions.ObjectModelReadWrite)]
    public class CheckPassword : Command<bool>
    {
        /// <summary>
        /// Password to check
        /// </summary>
        public string Password { get; set; }
    }
}
