using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.Shared
{
    /// <summary>
    /// Header describing the content of a full SPI transfer
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 12)]
    public struct TransferHeader
    {
        /// <summary>
        /// Unique number representing the format used for this type of data transfer
        /// </summary>
        /// <seealso cref="Consts.FormatCode"/>
        public byte FormatCode;

        /// <summary>
        /// Number of packets in the data transfer
        /// </summary>
        public byte NumPackets;

        /// <summary>
        /// Version of the protocol. This is incremented whenever the protocol details change
        /// </summary>
        /// <seealso cref="Consts.ProtocolVersion"/>
        public ushort ProtocolVersion;
        
        /// <summary>
        /// Sequence number (auto-incremented), used to detect resets on either side
        /// </summary>
        public ushort SequenceNumber;

        /// <summary>
        /// Total length of the data transfer in bytes
        /// </summary>
        public ushort DataLength;
        
        /// <summary>
        /// CRC16 checksum of the transfer data (reserved)
        /// </summary>
        public ushort ChecksumData;

        /// <summary>
        /// CRC16 checksum of the transfer header (reserved)
        /// </summary>
        public ushort ChecksumHeader;
    }
}