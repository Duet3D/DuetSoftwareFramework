namespace DuetAPI.Commands
{
    /// <summary>
    /// Wait for all pending codes of the given channel to finish
    /// </summary>
    /// <remarks>At present, this command only waits for codes interacting with the firmware.</remarks>
    public class Flush : Command
    {
        /// <summary>
        /// Code channel to wait for
        /// </summary>
        public CodeChannel Channel { get; set; }
    }
}
