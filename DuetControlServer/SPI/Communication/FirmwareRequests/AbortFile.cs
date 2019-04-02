using DuetAPI.Commands;
using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.FirmwareRequests
{
    /// <summary>
    /// Request abort of the currently executing files
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct AbortFileRequest
    {
        /// <summary>
        /// Code channel running the file(s)
        /// </summary>
        public byte Channel;
    }
}
