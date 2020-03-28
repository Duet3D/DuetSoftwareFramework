namespace DuetControlServer.SPI.Communication.Shared
{
    /// <summary>
    /// Result code for header and data transfers
    /// </summary>
    public enum TransferResponse : uint
    {
        /// <summary>
        /// Transfer is OK
        /// </summary>
        Success = 1,

        /// <summary>
        /// Bad transfer format
        /// </summary>
        BadFormat = 2,

        /// <summary>
        /// Bad protocol version
        /// </summary>
        BadProtocolVersion = 3,

        /// <summary>
        /// Bad data length
        /// </summary>
        BadDataLength = 4,

        /// <summary>
        /// Bad header checksum
        /// </summary>
        BadHeaderChecksum = 5,

        /// <summary>
        /// Bad header checksum
        /// </summary>
        BadDataChecksum = 6,

        /// <summary>
        /// Bad response. This one is special because it can follow a response exchange
        /// </summary>
        BadResponse = 0xFEFEFEFE,

        /// <summary>
        /// Error response because the pin is always low
        /// </summary>
        LowPin = 0,

        /// <summary>
        /// Error response because the pin is always high
        /// </summary>
        HighPin = 0xFFFFFFFF
    }
}
