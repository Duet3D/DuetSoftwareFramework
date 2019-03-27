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
        /// Used protocol version. This is incremented whenever the protocol details change
        /// </summary>
        public const ushort ProtocolVersion = 1;
        
        /// <summary>
        /// Number of RepRapFirmware modules that can be queried via <see cref="LinuxRequests.GetObjectModel"/>
        /// </summary>
        public const byte NumModules = 10;
    }
}