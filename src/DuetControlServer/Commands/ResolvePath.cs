using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.ResolvePath"/> command
    /// </summary>
    public class ResolvePath : DuetAPI.Commands.ResolvePath
    {
        /// <summary>
        /// Resolve the given RepRapFirmware-style filename to an absolute path
        /// </summary>
        /// <returns>Absolute file path</returns>
        public override Task<string> Execute() => FilePath.ToPhysicalAsync(Path);
    }
}