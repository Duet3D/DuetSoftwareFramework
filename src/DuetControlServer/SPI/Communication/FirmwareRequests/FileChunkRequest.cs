using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.FirmwareRequests
{
    /// <summary>
    /// Request abort of the currently executing files
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FileChunkRequest
    {
        /// <summary>
        /// Offset in the file
        /// </summary>
        public uint Offset;

        /// <summary>
        /// Maximum length of the file chunk to return
        /// </summary>
        public uint MaxLength;

        /// <summary>
        /// Length of the filename
        /// </summary>
        public uint FilenameLength;
    }
}
