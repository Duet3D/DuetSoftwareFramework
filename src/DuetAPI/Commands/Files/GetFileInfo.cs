using DuetAPI.ObjectModel;
using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Analyse a G-code file and return an instance of <see cref="ParsedFileInfo"/> when ready
    /// </summary>
    [RequiredPermissions(SbcPermissions.CommandExecution | SbcPermissions.FileSystemAccess | SbcPermissions.ReadGCodes)]
    public class GetFileInfo : Command<ParsedFileInfo>
    {
        /// <summary>
        /// The filename to extract information from
        /// </summary>
        public string FileName { get; set; }
    }
}