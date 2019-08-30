using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.LinuxRequests
{
    /// <summary>
    /// Response to a <see cref="FirmwareRequests.FileChunkRequest"/>.
    /// This is followed by the payload if Length is greater than 0
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FileChunk
    {
        /// <summary>
        /// Length of the file chunk or -1 if an error occurred
        /// </summary>
        public int DataLength;

        /// <summary>
        /// Total length of the file
        /// </summary>
        public uint FileLength;
    }
}