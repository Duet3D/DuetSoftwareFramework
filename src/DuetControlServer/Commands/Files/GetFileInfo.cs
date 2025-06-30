using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using DuetControlServer.Files.Parser;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.GetFileInfo"/> command
/// </summary>
/// <param name="fileInfoParser">File info parser</param>
public sealed class GetFileInfo(FileInfoParser fileInfoParser) : DuetAPI.Commands.GetFileInfo
{
    /// <summary>
    /// Retrieves file information from the given filename
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>File info</returns>
    public override Task<GCodeFileInfo> ExecuteAsync(CancellationToken cancellationToken = default) => fileInfoParser.ParseAsync(FileName, ReadThumbnailContent, cancellationToken);
}
