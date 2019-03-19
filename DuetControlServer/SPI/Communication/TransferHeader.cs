using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct TransferHeader
    {
        public byte FormatCode;
        public ushort Length;
        public byte Padding;
        public uint Checksum;
    }
}