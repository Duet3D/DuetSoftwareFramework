using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Uninstall a system package
    /// </summary>
    [RequiredPermissions(SbcPermissions.SuperUser)]
    public class UninstallSystemPackage : Command
    {
        /// <summary>
        /// Identifier of the package
        /// </summary>
        public string Package { get; set; }
    }
}
