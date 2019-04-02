using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.FirmwareRequests
{
    /// <summary>
    /// Response to a <see cref="LinuxRequests.Request.Code"/> request
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct CodeReply
    {
        /// <summary>
        /// Message type describing the message
        /// </summary>
        /// <seealso cref="MessageType"/>
        public uint MessageType;
    }
}