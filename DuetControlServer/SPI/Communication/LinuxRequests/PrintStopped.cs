using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.LinuxRequests
{
    /// <summary>
    /// Reason why the print has stopped
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

    /// <summary>
    /// Header for print stop notifications
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct PrintStopped
    {
        PrintStoppedReason Reason;
    }
}
