using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.Shared
{
    /// <summary>
    /// Header used for single packets from and to the RepRapFirmware board
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 8)]
    public struct PacketHeader
    {
        /// <summary>
        /// Identifier of this request
        /// </summary>
        /// <seealso cref="FirmwareRequests.Request"/>
        /// <seealso cref="LinuxRequests.Request"/>
        public ushort Request;

        /// <summary>
        /// Identifier of the packet
        /// </summary>
        public ushort Id;

        /// <summary>
        /// Length of the packet in bytes
        /// </summary>
        public ushort Length;

        /// <summary>
        /// Identifier of the packet that is supposed to be resend (defaults to 0)
        /// </summary>
        public ushort ResendPacketId;
    }
}
