using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Resolve a RepRapFirmware-style path to an actual file path
    /// </summary>
    [RequiredPermissions(SbcPermissions.CommandExecution | SbcPermissions.FileSystemAccess)]
    public class ResolvePath : Command<string>
    {
        /// <summary>
        /// Path that is RepRapFirmware-compatible
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// Optional base directory to resolve the path relative to
        /// </summary>
        public FileDirectory? BaseDirectory { get; set; }
    }
}