namespace DuetAPI.Commands
{
    /// <summary>
    /// Wait for all pending codes of the given channel to finish.
    /// This effectively guarantees that all buffered codes are processed by RRF before this command finishes.
    /// If the flush request is successful, true is returned
    /// </summary>
    public class Flush : Command<bool>
    {
        /// <summary>
        /// Code channel to flush
        /// </summary>
        public CodeChannel Channel { get; set; }
    }
}
