using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.FirmwareRequests
{
    /// <summary>
    /// Request abort of the currently executing files
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 8)]
    public struct ReadFileHeader
    {
        /// <summary>
        /// Handle of the file to read from
        /// </summary>
        public uint Handle;

        /// <summary>
        /// Maximum length to read
        /// </summary>
        public uint MaxLength;
    }
}
