using DuetControlServer.Files;
using System.Threading;
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
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Absolute file path</returns>
        public override Task<string> ExecuteAsync(CancellationToken cancellationToken = default) => (BaseDirectory != null) ? FilePath.ToPhysicalAsync(Path, BaseDirectory.Value, cancellationToken) : FilePath.ToPhysicalAsync(Path, cancellationToken: cancellationToken);
    }
}