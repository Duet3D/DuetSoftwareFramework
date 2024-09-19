namespace DuetHttpClient.Connector.Responses
{
    /// <summary>
    /// Response from a connect request in SBC mode
    /// </summary>
    internal class RestConnectResponse
    {
        /// <summary>
        /// Session key
        /// </summary>
        public string SessionKey { get; set; } = string.Empty;
    }
}