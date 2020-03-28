using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.FirmwareRequests
{
    /// <summary>
    /// Request abort of the currently executing files
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct AbortFileHeader
    {
        /// <summary>
        /// Code channel running the file(s)
        /// </summary>
        public byte Channel;

        /// <summary>
        /// Indicates if all pending files are supposed to be aborted
        /// </summary>
        public byte AbortAll;
    }
}
