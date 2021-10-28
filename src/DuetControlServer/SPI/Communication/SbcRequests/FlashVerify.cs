using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.SbcRequests
{
    /// <summary>
    /// Used as the last message to check if the firmware has been flashed successfully
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 8)]
    internal struct FlashVerify
    {
        /// <summary>
        /// Length of the flashed firmware
        /// </summary>
        public uint firmwareLength;

        /// <summary>
        /// CRC16 checksum of the firmware binary
        /// </summary>
        public ushort crc16;
    }
}
