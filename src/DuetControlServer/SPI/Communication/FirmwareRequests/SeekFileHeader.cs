using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.FirmwareRequests
{
    /// <summary>
    /// Seek to a new file position
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 8)]
    public struct SeekFileHeader
    {
        /// <summary>
        /// Handle of the file to write to
        /// </summary>
        public uint Handle;

        /// <summary>
        /// File position to go to
        /// </summary>
        public uint Offset;
    }
}
