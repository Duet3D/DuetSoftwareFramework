using DuetControlServer.Files;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.ResolvePath"/> command
    /// </summary>
    public sealed class ResolvePath : DuetAPI.Commands.ResolvePath
    {
        /// <summary>
        /// Resolve the given RepRapFirmware-style filename to an absolute path
        /// </summary>
        /// <returns>Absolute file path</returns>
        public override Task<string> Execute() => (BaseDirectory != null) ? FilePath.ToPhysicalAsync(Path, BaseDirectory.Value) : FilePath.ToPhysicalAsync(Path);
    }
}