namespace DuetControlServer.SPI.Communication.Shared
{
    /// <summary>
    /// Reason why the print has been stopped
    /// </summary>
    public enum PrintStoppedReason : byte
    {
        /// <summary>
        /// Print has finished successfully
        /// </summary>
        NormalCompletion = 0,

        /// <summary>
        /// User has cancelled the print
        /// </summary>
        UserCancelled = 1,

        /// <summary>
        /// Print has been aborted
        /// </summary>
        Abort = 2
    }
}
