using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.SharedRequests
{
    /// <summary>
    /// Request the retransmission of a packet received last time
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct Resend
    {
        /// <summary>
        /// ID of the packet to request
        /// </summary>
        public ushort PacketId;
    }
}