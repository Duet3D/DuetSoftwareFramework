using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.GetFileInfo"/> command
    /// </summary>
    public sealed class GetFileInfo : DuetAPI.Commands.GetFileInfo
    {
        /// <summary>
        /// Retrieves file information from the given filename
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>File info</returns>
        public override Task<GCodeFileInfo> ExecuteAsync(CancellationToken cancellationToken = default) => InfoParser.ParseAsync(FileName, ReadThumbnailContent);
    }
}