using DuetControlServer.SPI.Communication.Shared;
using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.SbcRequests
{
    /// <summary>
    /// Header of print stop notifications
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct PrintStoppedHeader
    {
        /// <summary>
        /// Reason why the print has been stopped
        /// </summary>
        public PrintStoppedReason Reason;
    }
}
