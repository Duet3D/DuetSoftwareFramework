namespace DuetAPI.Commands
{
    /// <summary>
    /// Analyse a G-code file and return an instance of <see cref="ParsedFileInfo"/> when ready
    /// </summary>
    public class GetFileInfo : Command<ParsedFileInfo>
    {
        /// <summary>
        /// The filename to extract information from
        /// </summary>
        public string FileName { get; set; }
    }
}