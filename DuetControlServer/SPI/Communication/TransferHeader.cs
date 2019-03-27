using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication
{
    /// <summary>
    /// Header describing the content of a full SPI transfer
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 12)]
    public struct TransferHeader
    {
        /// <summary>
        /// Unique number representing the format used for this type of data transfer
        /// </summary>
        /// <seealso cref="Consts.FormatCode"/>
        public byte FormatCode;

        /// <summary>
        /// Version of the protocol. This is incremented whenever the protocol details change
        /// </summary>
        /// <seealso cref="Consts.ProtocolVersion"/>
        public ushort ProtocolVersion;
        
        /// <summary>
        /// Length of the data transfer in bytes
        /// </summary>
        public byte NumPackets;

        /// <summary>
        /// Sequence number (auto-incremented), used to detect resets on either side
        /// </summary>
        public uint SequenceNumber;

        /// <summary>
        /// Total length of the data transfer in bytes
        /// </summary>
        public ushort Length;
        
        /// <summary>
        /// CRC16 checksum of the transfer header (reserved)
        /// </summary>
        public ushort Checksum;
    }
}