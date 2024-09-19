namespace DuetHttpClient.Connector.Responses
{
    /// <summary>
    /// Response to a G-code request
    /// </summary>
    internal class GcodeReply
    {
        /// <summary>
        /// Remaining buffer space in bytes
        /// </summary>
        public int Buff { get; set; }
    }
}