namespace DuetControlServer.SPI.Communication.Shared
{
    /// <summary>
    /// Result code for header and data transfers
    /// </summary>
    /// <remarks>
    /// This must remain a static class in order to allow for unknown enum values
    /// </remarks>
    public static class TransferResponse
    {
        /// <summary>
        /// Transfer is OK
        /// </summary>
        public const uint Success = 1;

        /// <summary>
        /// Bad transfer format
        /// </summary>
        public const uint BadFormat = 2;

        /// <summary>
        /// Bad protocol version
        /// </summary>
        public const uint BadProtocolVersion = 3;

        /// <summary>
        /// Bad data length
        /// </summary>
        public const uint BadDataLength = 4;

        /// <summary>
        /// Bad header checksum
        /// </summary>
        public const uint BadHeaderChecksum = 5;

        /// <summary>
        /// Bad header checksum
        /// </summary>
        public const uint BadDataChecksum = 6;

        /// <summary>
        /// Bad response. This one is special because it can follow a response exchange
        /// </summary>
        public const uint BadResponse = 0xFEFEFEFE;

        /// <summary>
        /// Error response because the pin is always low
        /// </summary>
        public const uint LowPin = 0;

        /// <summary>
        /// Error response because the pin is always high
        /// </summary>
        public const uint HighPin = 0xFFFFFFFF;
    }
}
