namespace DuetAPI.Commands
{
    /// <summary>
    /// Resolve a RepRapFirmware-style path to an actual file path
    /// </summary>
    public class ResolvePath : Command<string>
    {
        /// <summary>
        /// Path that is RepRapFirmware-compatible
        /// </summary>
        public string Path { get; set; }
    }
}