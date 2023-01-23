using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Wait for all pending (macro) codes on the given channel to finish.
    /// This effectively guarantees that all buffered codes are processed by RRF before this command finishes.
    /// If the flush request is successful, true is returned
    /// </summary>
    [RequiredPermissions(SbcPermissions.CommandExecution)]
    public class Flush : Command<bool>
    {
        /// <summary>
        /// Code channel to flush
        /// </summary>
        /// <remarks>
        /// This value is ignored if this request is processed while a code is being intercepted
        /// </remarks>
        public CodeChannel Channel { get; set; }

        /// <summary>
        /// Whether the File and File2 streams are supposed to synchronize if a code is being intercepted
        /// </summary>
        /// <remarks>
        /// This option should be used with care, under certain circumstances this can lead to a deadlock!
        /// </remarks>
        public bool SyncFileStreams { get; set; } = false;

        /// <summary>
        /// Check if the corresponding channel is actually executing codes (i.e. if it is active).
        /// If the input channel is not active, this command returns false
        /// </summary>
        /// <remarks>
        /// This option is ignored if <see cref="SyncFileStreams"/> is true
        /// </remarks>
        public bool IfExecuting { get; set; } = true;
    }
}
