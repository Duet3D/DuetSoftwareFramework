using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Install or upgrade a system package
    /// </summary>
    [RequiredPermissions(SbcPermissions.SuperUser)]
    public class InstallSystemPackage : Command
    {
        /// <summary>
        /// Absolute file path to the package file
        /// </summary>
        public string PackageFile { get; set; } = string.Empty;
    }
}
