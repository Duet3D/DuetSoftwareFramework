using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.LinuxRequests
{
    /// <summary>
    /// Response to a file open request
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 8)]
    public struct OpenFileResult
    {
        /// <summary>
        /// Handle of the opened file or <see cref="Consts.NoFileHandle"/> if the file could not be opened
        /// </summary>
        public uint Handle;

        /// <summary>
        /// Size of the file or 0 if it could not be opened
        /// </summary>
        public uint FileSize;
    }
}
