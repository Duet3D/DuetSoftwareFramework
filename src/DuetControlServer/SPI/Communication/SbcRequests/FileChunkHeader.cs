using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.SbcRequests
{
    /// <summary>
    /// Response to a <see cref="FirmwareRequests.FileChunkHeader"/>.
    /// This is followed by the payload if Length is greater than 0
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 8)]
    public struct FileChunkHeader
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