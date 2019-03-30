using System.Runtime.InteropServices;
using DuetControlServer.SPI.Communication.DuetRequests;

namespace DuetControlServer.SPI.Communication
{
    /// <summary>
    /// Header used for single packets from and to the RepRapFirmware board
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct PacketHeader
    {
        /// <summary>
        /// Identifier of this request
        /// </summary>
        /// <seealso cref="DuetRequests.Request"/>
        /// <seealso cref="LinuxRequests.Request"/>
        public ushort Request;

        /// <summary>
        /// Length of the packet in bytes
        /// </summary>
        public ushort Length;
    }
}
