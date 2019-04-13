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
        public const int Success = 1;

        /// <summary>
        /// Bad transfer format
        /// </summary>
        public const int BadFormat = 2;

        /// <summary>
        /// Bad protocol version
        /// </summary>
        public const int BadProtocolVersion = 3;

        /// <summary>
        /// Bad data length
        /// </summary>
        public const int BadDataLength = 4;

        /// <summary>
        /// Bad checksum
        /// </summary>
        public const int BadChecksum = 5;
    }
}
