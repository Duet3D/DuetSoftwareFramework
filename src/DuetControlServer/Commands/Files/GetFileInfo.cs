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
        /// <returns>File info</returns>
        public override Task<GCodeFileInfo> Execute() => InfoParser.Parse(FileName, ReadThumbnailContent);
    }
}