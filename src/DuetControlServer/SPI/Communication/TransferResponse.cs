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
        public const int Success = 0;

        /// <summary>
        /// Bad transfer format
        /// </summary>
        public const int BadFormat = -1;

        /// <summary>
        /// Bad protocol version
        /// </summary>
        public const int BadProtocolVersion = -2;

        /// <summary>
        /// Bad checksum
        /// </summary>
        public const int BadChecksum = -3;

        /// <summary>
        /// Request state reset
        /// </summary>
        /// <remarks>This must remain the last entry until checksums have been implemented</remarks>
        public const int RequestStateReset = -4;
    }
}
