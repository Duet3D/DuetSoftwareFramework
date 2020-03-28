using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.FirmwareRequests
{
    /// <summary>
    /// Request abort of the currently executing files
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct CodeBufferUpdateHeader
    {
        /// <summary>
        /// Bytes available for storing buffered codes
        /// </summary>
        public ushort BufferSpace;
    }
}
