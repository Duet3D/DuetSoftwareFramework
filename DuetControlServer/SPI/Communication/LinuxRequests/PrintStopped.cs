using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.LinuxRequests
{
    /// <summary>
    /// Header for print stop notifications
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct PrintStopped
    {
        /// <summary>
        /// Reason why the print has been stopped
        /// </summary>
        public byte Reason;
    }
}
