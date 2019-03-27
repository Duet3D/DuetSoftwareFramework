using System.Runtime.InteropServices;
using DuetControlServer.SPI.Communication.DuetRequests;

namespace DuetControlServer.SPI.Communication
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
        /// <seealso cref="DuetRequests.Request"/>
        /// <seealso cref="LinuxRequests.Request"/>
        public ushort Request;

        /// <summary>
        /// Identifier of the packet (auto-incrementing, reset after each transmission)
        /// </summary>
        public ushort PacketId;

        /// <summary>
        /// Length of the packet in bytes
        /// </summary>
        public ushort Length;

        /// <summary>
        /// CRC16 checksum of the packet header and payload (reserved for future use)
        /// </summary>
        public ushort Checksum;
    }
}
