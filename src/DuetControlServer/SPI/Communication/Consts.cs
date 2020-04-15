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
        public const ushort ProtocolVersion = 2;

        /// <summary>
        /// Size of a packet transfer buffer
        /// </summary>
        public const int BufferSize = 8192;

        /// <summary>
        /// Maximum size of a binary encoded G/M/T-code. This is limited by RepRapFirmware (see code queue)
        /// </summary>
        public const int MaxCodeBufferSize = 256;

        /// <summary>
        /// Maximum supported length of messages to be sent to RepRapFirmware
        /// </summary>
        public const int MaxMessageLength = 256;

        /// <summary>
        /// Maximum number of evaluation requests to send per transfer
        /// </summary>
        public const int MaxEvaluationRequestsPerTransfer = 32;

        /// <summary>
        /// Size of the header prefixing a buffered code
        /// </summary>
        public const int BufferedCodeHeaderSize = 4;

        /// <summary>
        /// Value used by RepRapFirmware to represent an invalid file position
        /// </summary>
        public const uint NoFilePosition = 0xFFFFFFFF;

        /// <summary>
        /// Size of each transmitted IAP binary segment (must be a multiple of IFLASH_PAGE_SIZE)
        /// </summary>
        public const int IapSegmentSize = 1536;

        /// <summary>
        /// Time to wait when the IAP reboots to the main firmware
        /// </summary>
        public const int IapBootDelay = 500;

        /// <summary>
        /// Size of each transmitted firmware binary segment (must be equal to blockReadSize in the IAP project)
        /// </summary>
        public const int FirmwareSegmentSize = 2048;

        /// <summary>
        /// Delay to await after the last firmware segment has been written (in ms)
        /// </summary>
        public const int FirmwareFinishedDelay = 500;

        /// <summary>
        /// Time to wait when the IAP reboots to the main firmware
        /// </summary>
        public const int IapRebootDelay = 2000;
    }
}