namespace DuetAPI.Connection.InitMessages;

/// <summary>
/// Enter code streaming connection mode.
/// In this connection mode G/M/T-codes are sent as text lines and processed asynchronously (up to <see cref="BufferSize"/>), and their replies are received as text.
/// To upload a file, send M559 or M560, wait for the "Writing to file" reply and then send the file content as raw bytes:
/// as many as the S parameter says, or up to the end-of-file string <c>&lt;!-- **EoF** --&gt;</c> if there is no S parameter.
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
