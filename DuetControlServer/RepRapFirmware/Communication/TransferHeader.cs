using System.Runtime.InteropServices;

namespace DuetControlServer.RepRapFirmware.Communication
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