namespace DuetHttpClient.Connector.Responses
{
    /// <summary>
    /// Generic reply to report if an error occurred
    /// </summary>
    internal class ErrResponse
    {
        /// <summary>
        /// Error code, 0 on success
        /// </summary>
        public int Err { get; set; }
    }
}
