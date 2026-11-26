using System;
using System.IO;

namespace DuetAPI.Commands;

/// <summary>
/// What the parser carries from one code to the next
/// </summary>
/// <param name="MidLine">Whether the next code is not the first on its line</param>
/// <param name="Indent">Indentation of the line, if the next code is not the first on it</param>
/// <param name="EnforcingAbsolutePosition">Whether the line started with G53</param>
/// <param name="LastGCode">Last major G-code to repeat, or null if there is none</param>
/// <param name="LineNumber">Current line number, or null if it is not known</param>
/// <remarks>The default is the start of a line about which nothing else is known</remarks>
public readonly record struct CodeParserState(bool MidLine, byte Indent, bool EnforcingAbsolutePosition, int? LastGCode, long? LineNumber);

/// <summary>
/// Internal buffer for reading asynchronously from files
/// </summary>
/// <param name="bufferSize">How many bytes to buffer when reading from a file</param>
/// <param name="isFile">Indicates if line numbers and file positions are valid</param>
public sealed class CodeParserBuffer(int bufferSize, bool isFile)
{
    /// <summary>
    /// Indicates if a NL was seen before
    /// </summary>
    internal bool SeenNewLine = true;

    /// <summary>
    /// Last indentation level
    /// </summary>
    internal byte Indent;

    /// <summary>
    /// Whether the line started with G53
    /// </summary>
    internal bool EnforcingAbsolutePosition;

    /// <summary>
    /// Buffer content
    /// </summary>
    internal readonly byte[] Content = new byte[bufferSize];

    /// <summary>
    /// Pointer in the buffer
    /// </summary>
    internal int Pointer;

    /// <summary>
    /// How many bytes are available for reading
    /// </summary>
    internal int Size;

    /// <summary>
    /// Invalidate the buffer internally
    /// </summary>
    internal void InvalidateData()
    {
        SeenNewLine = true;
        Indent = 0;
        EnforcingAbsolutePosition = false;
    }

    /// <summary>
    /// State the next code is read in
    /// </summary>
    /// <remarks><see cref="Invalidate"/> loses it, so reading a code again needs it put back</remarks>
    public CodeParserState State
    {
        get => new(!SeenNewLine, Indent, EnforcingAbsolutePosition, (LastGCode < 0) ? null : LastGCode, LineNumber);
        set
        {
            SeenNewLine = !value.MidLine;
            Indent = value.Indent;
            EnforcingAbsolutePosition = value.EnforcingAbsolutePosition;
            LastGCode = value.LastGCode ?? -1;
            LineNumber = value.LineNumber;
        }
    }

    /// <summary>
    /// Indicates if this buffer is used for reading from a file
    /// </summary>
    internal bool IsFile = isFile;

    /// <summary>
    /// Current line number
    /// </summary>
    public long? LineNumber = isFile ? (long?)1 : null;

    /// <summary>
    /// Last major G-code to repeat
    /// </summary>
    public int LastGCode = -1;

    /// <summary>
    /// Indicates if the last code may be repeated as per Fanuc or LaserWeb style
    /// </summary>
    public bool MayRepeatCode;

    /// <summary>
    /// Invalidate the buffer
    /// </summary>
    public void Invalidate()
    {
        InvalidateData();
        Pointer = Size = 0;
        LineNumber = null;
        LastGCode = -1;
    }

    /// <summary>
    /// Get the actual byte position when reading from a stream
    /// </summary>
    /// <param name="stream">Stream to read from</param>
    /// <returns>Actual position in bytes</returns>
    public long GetPosition(Stream stream) => stream.Position - Size + Pointer;
}
