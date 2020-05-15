using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.Shared
{
    /// <summary>
    /// Header for arbitrary messages
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 8)]
    public struct MessageHeader
    {
        /// <summary>
        /// Message type describing the message
        /// </summary>
        /// <seealso cref="MessageType"/>
        public MessageTypeFlags MessageType;

        /// <summary>
        /// Length of the reply in bytes
        /// </summary>
        public ushort Length;
    }
}