namespace DuetAPI.Connection.InitMessages
{
    /// <summary>
    /// Enter code streaming connection mode
    /// In this connection mode G/M/T-codes are processed asynchronously (up to <see cref="BufferSize"/>)
    /// </summary>
    public sealed class CodeStreamInitMessage : ClientInitMessage
    {
        /// <summary>
        /// Maximum number of codes being executed simultaneously
        /// </summary>
        public int BufferSize { get; set; } = Defaults.CodeBufferSize;

        /// <summary>
        /// Destination channel for incoming codes
        /// </summary>
        public CodeChannel Channel { get; set; } = Defaults.InputChannel;

        /// <summary>
        /// Creates a new init message instance
        /// </summary>
        public CodeStreamInitMessage() => Mode = ConnectionMode.CodeStream;
    }
}