using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.FirmwareRequests
{
    /// <summary>
    /// Plain file handle header
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct FileHandleHeader
    {
        /// <summary>
        /// Handle of the file
        /// </summary>
        public uint Handle;
    }
}
