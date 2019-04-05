using System.Threading.Tasks;
using DuetAPI;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the GetFileInfo command
    /// </summary>
    public class GetFileInfo : DuetAPI.Commands.GetFileInfo
    {
        /// <summary>
        /// Retrieves file information from the given filename
        /// </summary>
        /// <returns>File info</returns>
        protected override Task<ParsedFileInfo> Run() => FileInfoParser.Parse(FileName);
    }
}