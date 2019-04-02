using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.FirmwareRequests
{
    /// <summary>
    /// Header for print pause events
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 8)]
    public struct PrintPaused
    {
        /// <summary>
        /// Position at which the file has been paused
        /// </summary>
        public uint FilePosition;

        /// <summary>
        /// Reason why the print has been paused
        /// </summary>
        /// <seealso cref="PrintPausedReason"/>
        public byte PauseReason;
    }
}