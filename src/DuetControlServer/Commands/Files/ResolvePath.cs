using DuetControlServer.Files;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.ResolvePath"/> command
/// </summary>
/// <param name="filePathResolver">File path resolver</param>
public sealed class ResolvePath(FilePathResolver filePathResolver) : DuetAPI.Commands.ResolvePath
{
    /// <summary>
    /// Resolve the given RepRapFirmware-style filename to an absolute path
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Absolute file path</returns>
    public override Task<string> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        return (BaseDirectory != null)
            ? filePathResolver.ToPhysicalAsync(Path, BaseDirectory.Value, cancellationToken)
            : filePathResolver.ToPhysicalAsync(Path, cancellationToken: cancellationToken);
    }
}
