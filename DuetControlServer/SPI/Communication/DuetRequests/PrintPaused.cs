using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.DuetRequests
{
    /// <summary>
    /// Header for print pause events
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct PrintPaused
    {
        /// <summary>
        /// Position at which the file has been paused
        /// </summary>
        public uint FilePosition;
    }
}