namespace DuetHttpClient.Connector.Responses
{
    /// <summary>
    /// Reply for a rr_connect request in standalone mode
    /// </summary>
    internal class PollConnectResponse : ErrResponse
    {
        /// <summary>
        /// Indicates if the endpoint is emulated
        /// </summary>
        public bool IsEmulated { get; set; }

        /// <summary>
        /// API level of the firmware
        /// </summary>
        public int ApiLevel { get; set; }

        /// <summary>
        /// Optional session key (if supported by the firmware)
        /// </summary>
        public uint? SessionKey { get; set; }
    }
}
