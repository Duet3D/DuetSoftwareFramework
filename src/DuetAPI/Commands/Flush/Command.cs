namespace DuetAPI.Commands
{
    /// <summary>
    /// Wait for all pending codes of the given channel to finish.
    /// If the flush command was successful, this command returns true
    /// </summary>
    public class Flush : Command<bool>
    {
        /// <summary>
        /// Code channel to wait for
        /// </summary>
        public CodeChannel Channel { get; set; }
    }
}
