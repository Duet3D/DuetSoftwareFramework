namespace DuetControlServer.SPI.Communication
{
    /// <summary>
    /// Result code of header and data transfers
    /// </summary>
    public class TransferResponse
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
    }
}
