using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.FirmwareRequests
{
    /// <summary>
    /// Response to a <see cref="LinuxRequests.Request.Code"/> request
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 8)]
    public struct CodeReply
    {
        /// <summary>
        /// Message type describing the message
        /// </summary>
        /// <seealso cref="MessageType"/>
        public uint MessageType;

        /// <summary>
        /// Length of the reply in bytes
        /// </summary>
        public ushort Length;
    }
}