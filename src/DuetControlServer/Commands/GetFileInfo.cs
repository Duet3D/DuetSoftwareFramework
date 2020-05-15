using System.Threading.Tasks;
using DuetAPI.Machine;
using DuetControlServer.Files;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.GetFileInfo"/> command
    /// </summary>
    public class GetFileInfo : DuetAPI.Commands.GetFileInfo
    {
        /// <summary>
        /// Retrieves file information from the given filename
        /// </summary>
        /// <returns>File info</returns>
        public override Task<ParsedFileInfo> Execute() => InfoParser.Parse(FileName);
    }
}