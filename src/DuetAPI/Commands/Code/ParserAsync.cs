using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DuetAPI.Commands;

public partial class Code
{
    /// <summary>
    /// Parse the next available G/M/T-code from the given stream asynchronously
    /// </summary>
    /// <param name="stream">Stream to read from</param>
    /// <param name="result">Code to fill</param>
    /// <param name="buffer">Internal buffer for parsing codes</param>
    /// <param name="cancellationToken">Cancellation token instance</param>
    /// <returns>Whether anything could be read</returns>
    /// <exception cref="ArgumentException">BOM from start of file showed that this file is neither ASCII nor UTF-8</exception>
    /// <exception cref="CodeParserException">Thrown if the code contains errors like unterminated strings or unterminated comments</exception>
    public static async ValueTask<bool> ParseAsync(Stream stream, Code result, CodeParserBuffer buffer, CancellationToken cancellationToken = default)
    {
        async ValueTask FillBufferAsync()
        {
#if NETSTANDARD2_1_OR_GREATER || NET6_0_OR_GREATER
            buffer.Size = await stream.ReadAsync(buffer.Content, cancellationToken).ConfigureAwait(false);
#else
            buffer.Size = await stream.ReadAsync(buffer.Content, 0, buffer.Content.Length, cancellationToken).ConfigureAwait(false);
#endif
            buffer.Pointer = 0;
        }

        // Deal with BOM when starting to parse a file. Previously this was done by the used StreamReader instance
        if (buffer.IsFile && stream.Position + buffer.Pointer == 0)
        {
            await FillBufferAsync().ConfigureAwait(false);

            if (buffer.Size >= 2 && buffer.Content[0] == 0xFF && (buffer.Content[1] & 0xFE) == 0xFE)
            {
                throw new ArgumentException("Cannot parse codes from UTF-16 files. Use UTF-8 or ASCII instead");
            }
            else if (buffer.Size >= 3 && buffer.Content[0] == 0xEF && buffer.Content[1] == 0xBB && buffer.Content[2] == 0xBF)
            {
                // Skip BOM in UTF-8 files
                buffer.Pointer = 3;
            }
            else if (buffer.Size >= 4)
            {
                if ((buffer.Content[0] == 0x00 && buffer.Content[1] == 0x00 && buffer.Content[2] == 0xFF && buffer.Content[3] == 0xFF) ||
                    (buffer.Content[0] == 0xFF && buffer.Content[1] == 0xFF && buffer.Content[2] == 0x00 && buffer.Content[3] == 0x00))
                {
                    throw new ArgumentException("Cannot parse codes from UTF-32 files. Use UTF-8 or ASCII instead");
                }
                if (buffer.Content[0] == 0x2B && buffer.Content[1] == 0x2F && buffer.Content[2] == 0x76 && buffer.Content[3] is 0x38 or 0x39 or 0x2B or 0x2F)
                {
                    throw new ArgumentException("Cannot parse codes from UTF-7 files. Use UTF-8 or ASCII instead");
                }
            }
        }

        // Start parsing
        ParserState state = new()
        {
            ReadingAtStart = buffer.SeenNewLine,
            MayRepeatCode = buffer.MayRepeatCode
        };
        buffer.SeenNewLine = false;

        result.Flags = buffer.EnforcingAbsolutePosition ? CodeFlags.EnforceAbsolutePosition : CodeFlags.None;
        result.Indent = buffer.Indent;
        result.Length = 0;
        result.FilePosition = buffer.IsFile ? buffer.GetPosition(stream) : null;
        result.LineNumber = buffer.LineNumber;

        char c;
        do
        {
            // Read the next character
            if (buffer.Pointer >= buffer.Size)
            {
                await FillBufferAsync().ConfigureAwait(false);
            }
            c = (buffer.Pointer < buffer.Size) ? (char)buffer.Content[buffer.Pointer] : '\n';
            result.Length++;
            buffer.Pointer++;

            if (c == '\n' && !state.HadLineNumber && buffer.LineNumber is not null)
            {
                // Keep track of the line number (if possible)
                buffer.LineNumber++;
            }
            if (c == '\r')
            {
                // Ignore CR
                continue;
            }

            // Stop if another G/M/T code is coming up and this one is complete
            if (StartsNextCode(state, result, c))
            {
                // The character belongs to the next code, so put it back
                buffer.Pointer--;
                break;
            }

            // Peek at the next character without consuming it
            if (buffer.Pointer >= buffer.Size)
            {
                await FillBufferAsync().ConfigureAwait(false);
            }
            char peek = (buffer.Pointer < buffer.Size) ? (char)buffer.Content[buffer.Pointer] : '\0';

            if (ProcessCharacter(state, result, c, peek))
            {
                buffer.Pointer++;
                result.Length++;
            }

            // Carry the state that persists across codes on the same line back to the buffer
            buffer.Indent = result.Indent;
            if (result.Flags.HasFlag(CodeFlags.EnforceAbsolutePosition))
            {
                buffer.EnforcingAbsolutePosition = true;
            }
            if (result.Flags.HasFlag(CodeFlags.HasExplicitLineNumber))
            {
                buffer.LineNumber = result.LineNumber;
            }
        }
        while (c != '\n');

        // Reset the buffer state once the line has been fully read
        if (c is '\n' or '\0')
        {
            buffer.InvalidateData();
        }

        // Deal with Fanuc and LaserWeb G-code styles
        if (state.MayRepeatCode)
        {
            if (result.Type == CodeType.GCode && result.MajorNumber is not null)
            {
                buffer.LastGCode = result.MajorNumber.Value;
            }
            else if (result.Type == CodeType.None &&
                        buffer.LastGCode is 0 or 1 or 2 or 3 &&
                        result.Parameters.Any(parameter => ObjectModel.Axis.Letters.Contains(parameter.Letter)))
            {
                result.Type = CodeType.GCode;
                result.MajorNumber = buffer.LastGCode;
            }
            else
            {
                buffer.LastGCode = -1;
            }
        }

        FinishCode(state, result, c);
        return state.ContentRead;
    }
}
