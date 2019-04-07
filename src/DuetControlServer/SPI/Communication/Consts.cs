namespace DuetControlServer.SPI.Communication
{
    /// <summary>
    /// Static class holding SPI transfer constants
    /// </summary>
    public static class Consts
    {
        /// <summary>
        /// Unique format code for binary SPI transfers
        /// </summary>
        /// <remarks>Must be different from any other used format code (0x3E = DuetWiFiServer)</remarks>
        public const byte FormatCode = 0x5F;

        /// <summary>
        /// Unique format code that is not used anywhere else
        /// </summary>
        public const byte InvalidFormatCode = 0xC9;
        
        /// <summary>
        /// Used protocol version. This is incremented whenever the protocol details change
        /// </summary>
        public const ushort ProtocolVersion = 1;

        /// <summary>
        /// Size of a packet transfer buffer
        /// </summary>
        public const int BufferSize = 2048;

        /// <summary>
        /// Number of RepRapFirmware modules that can be queried via <see cref="LinuxRequests.Request.GetObjectModel"/>
        /// </summary>
        public const byte NumModules = 3;

        /// <summary>
        /// Maximum size of a binary encoded G/M/T-code. This is limited by RepRapFirmware
        /// </summary>
        public const int MaxCodeBufferSize = 192;
    }
}