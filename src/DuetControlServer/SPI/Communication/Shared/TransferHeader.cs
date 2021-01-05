using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.Shared
{
    /// <summary>
    /// Header describing the content of a full SPI transfer
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct TransferHeader
    {
        /// <summary>
        /// Unique number representing the format used for this type of data transfer
        /// </summary>
        /// <seealso cref="Consts.FormatCode"/>
        [FieldOffset(0)]
        public byte FormatCode;

        /// <summary>
        /// Number of packets in the data transfer
        /// </summary>
        [FieldOffset(1)]
        public byte NumPackets;

        /// <summary>
        /// Version of the protocol. This is incremented whenever the protocol details change
        /// </summary>
        /// <seealso cref="Consts.ProtocolVersion"/>
        [FieldOffset(2)]
        public ushort ProtocolVersion;
        
        /// <summary>
        /// Sequence number (auto-incremented), used to detect resets on either side
        /// </summary>
        [FieldOffset(4)]
        public ushort SequenceNumber;

        /// <summary>
        /// Total length of the data transfer in bytes
        /// </summary>
        [FieldOffset(6)]
        public ushort DataLength;
        
        /// <summary>
        /// CRC16 checksum of the transfer data (protocol version < 4)
        /// </summary>
        [FieldOffset(8)]
        public ushort ChecksumData16;

        /// <summary>
        /// CRC16 checksum of the transfer header (protocol version < 4)
        /// </summary>
        [FieldOffset(10)]
        public ushort ChecksumHeader16;

        /// <summary>
        /// CRC32 checksum of the transfer data (protocol version >= 4)
        /// </summary>
        [FieldOffset(8)]
        public uint ChecksumData32;

        /// <summary>
        /// CRC32 checksum of the transfer header (protocol version >= 4)
        /// </summary>
        [FieldOffset(12)]
        public uint ChecksumHeader32;
    }
}