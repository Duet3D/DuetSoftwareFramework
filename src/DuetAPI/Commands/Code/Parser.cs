using DuetAPI.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace DuetAPI.Commands;

public partial class Code
{
    // Numeric parameters may hold only characters of this string
    private const string NumericParameterChars = "01234567890+-.";

    /// <summary>
    /// Mutable state shared by the synchronous and asynchronous parsers while decoding a single code.
    /// The parsing logic itself lives in <see cref="ProcessCharacter"/>; the wrappers only provide
    /// the character source and the surrounding stream/file bookkeeping.
    /// </summary>
    private sealed class ParserState
    {
        public char Letter;
        public bool ContentRead, UnprecedentedParameter;
        public bool InFinalComment, InEncapsulatedComment, InChunk, InSingleQuotes, InDoubleQuotes, InExpression, InKeywordArgument;
        public bool ReadingAtStart, IsLineNumber, HadLineNumber, IsNumericParameter, EndingChunk;
        public bool NextCharLowerCase, WasQuoted, WasExpression;
        public int NumCurlyBraces, NumRoundBraces;

        /// <summary>
        /// Whether the last code may be repeated as per Fanuc or LaserWeb style (async only)
        /// </summary>
        public bool MayRepeatCode;

        // Parameter values and keyword arguments accumulate here as raw UTF-8 bytes and are only
        // decoded when a chunk completes. Comments accumulate separately because a single code may
        // carry an encapsulated comment and a final comment at once.
        private readonly List<byte> _value = [];
        private readonly List<byte> _comment = [];

        /// <summary>
        /// Whether a comment (possibly empty) was seen, so the resulting comment is non-null
        /// </summary>
        public bool HadComment;

        public int ValueLength => _value.Count;

        public void AddToValue(char c) => _value.Add((byte)c);

        public void ClearValue() => _value.Clear();

#if NET5_0_OR_GREATER
        public string GetValue() => Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(_value));
#else
        public string GetValue() => Encoding.UTF8.GetString(_value.ToArray());
#endif

        public void SetValue(string value)
        {
            _value.Clear();
            _value.AddRange(Encoding.UTF8.GetBytes(value));
        }

        /// <summary>
        /// Check if the current value contains the given ASCII character
        /// </summary>
        public bool ValueContains(char c)
        {
            byte b = (byte)c;
            return _value.Contains(b);
        }

        /// <summary>
        /// Check if the current value ends with a colon, ignoring trailing whitespace
        /// </summary>
        public bool ValueTrimEndEndsWithColon()
        {
            for (int i = _value.Count - 1; i >= 0; i--)
            {
                char c = (char)_value[i];
                if (char.IsWhiteSpace(c))
                {
                    continue;
                }
                return c == ':';
            }
            return false;
        }

        public void AddToComment(char c)
        {
            _comment.Add((byte)c);
            HadComment = true;
        }

#if NET5_0_OR_GREATER
        public string? GetComment() => HadComment ? Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(_comment)) : null;
#else
        public string? GetComment() => HadComment ? Encoding.UTF8.GetString(_comment.ToArray()) : null;
#endif
    }

    /// <summary>
    /// Process a single character through the shared parser state machine
    /// </summary>
    /// <param name="state">Current parser state</param>
    /// <param name="result">Code being filled</param>
    /// <param name="c">Character to process (a single UTF-8 byte for non-ASCII content)</param>
    /// <param name="peek">Next character that follows, or '\0' if none</param>
    /// <returns>Whether the <paramref name="peek"/> character was consumed as well</returns>
    /// <exception cref="CodeParserException">Thrown if the code contains errors like unterminated strings or comments</exception>
    private static bool ProcessCharacter(ParserState state, Code result, char c, char peek)
    {
        bool consumedPeek = false;

        if (state.InFinalComment)
        {
            // Reading a comment ending the current line
            if (c != '\n')
            {
                // Add next character to the comment unless it is the "artificial" 0-character termination
                state.AddToComment(c);
            }
            else
            {
                // Something started a comment, so the comment cannot be null any more
                state.HadComment = true;
            }
            return consumedPeek;
        }

        if (state.InEncapsulatedComment)
        {
            // Reading an encapsulated comment in braces
            if (c != ')')
            {
                // Add next character to the comment
                state.AddToComment(c);
            }
            else
            {
                // End of encapsulated comment, it cannot be null any more
                state.HadComment = true;
                state.InEncapsulatedComment = false;
            }
            return consumedPeek;
        }

        if (state.InKeywordArgument)
        {
            if (state.InSingleQuotes)
            {
                // Add next character to the parameter value
                state.AddToValue(c);

                if (c == '\'')
                {
                    if (peek == '\'')
                    {
                        // Subsequent single quotes are treated as a single quote char
                        state.AddToValue(c);
                        consumedPeek = true;
                    }
                    state.InSingleQuotes = false;
                }
            }
            else if (state.InDoubleQuotes)
            {
                // Add next character to the parameter value
                state.AddToValue(c);

                if (c == '"')
                {
                    if (peek == '"')
                    {
                        // Subsequent double quotes are treated as a single quote char
                        state.AddToValue(c);
                        consumedPeek = true;
                    }
                    else
                    {
                        // No longer in an escaped parameter
                        state.InDoubleQuotes = false;
                    }
                }
            }
            else
            {
                switch (c)
                {
                    case '\n':
                        // Ignore final NL
                        break;
                    case '\'':
                        state.AddToValue('\'');
                        state.InSingleQuotes = true;
                        break;
                    case '"':
                        state.AddToValue('"');
                        state.InDoubleQuotes = true;
                        break;
                    case ';':
                        result.KeywordArgument = state.GetValue().Trim();
                        state.ClearValue();
                        state.InKeywordArgument = false;
                        state.InFinalComment = true;
                        state.HadComment = true;
                        break;
                    case '{':
                        state.AddToValue('{');
                        state.NumCurlyBraces++;
                        break;
                    case '}':
                        state.AddToValue('}');
                        state.NumCurlyBraces--;
                        break;
                    case '(':
                        state.AddToValue('(');
                        state.NumRoundBraces++;
                        break;
                    case ')':
                        if (state.NumRoundBraces > 0)
                        {
                            state.AddToValue(')');
                            state.NumRoundBraces--;
                        }
                        else
                        {
                            throw new CodeParserException("Unexpected closing round brace", result);
                        }
                        break;
                    default:
                        if (!char.IsWhiteSpace(c) || state.InKeywordArgument)
                        {
                            // In fact, it should be possible to leave out whitespaces here but we here don't check for quoted strings yet
                            state.AddToValue(c);
                        }
                        break;
                }
            }

            if (state.InKeywordArgument)
            {
                return consumedPeek;
            }
        }

        if (state.InChunk)
        {
            if (state.InSingleQuotes)
            {
                if (c == '\'')
                {
                    if (peek == '\'')
                    {
                        // Treat subsequent single quotes as a single quote char
                        state.AddToValue('\'');
                        consumedPeek = true;
                    }
                    state.InSingleQuotes = false;
                    state.WasQuoted = true;
                    state.EndingChunk = true;
                }
                else
                {
                    // Add next character to the parameter value
                    state.AddToValue(c);
                }
            }
            else if (state.InDoubleQuotes)
            {
                if (c == '\'')
                {
                    if (state.NextCharLowerCase)
                    {
                        // Treat subsequent single-quotes as a single-quote char
                        state.AddToValue('\'');
                        state.NextCharLowerCase = false;
                    }
                    else
                    {
                        // Next letter should be lower-case
                        state.NextCharLowerCase = true;
                    }
                }
                else if (c == '"')
                {
                    if (peek == '"')
                    {
                        // Treat subsequent double quotes as a single double-quote char
                        state.AddToValue('"');
                        consumedPeek = true;
                    }
                    else
                    {
                        // No longer in an escaped parameter
                        state.InDoubleQuotes = state.NextCharLowerCase = false;
                        state.WasQuoted = true;
                        state.EndingChunk = true;
                    }
                }
                else if (state.NextCharLowerCase)
                {
                    // Add next lower-case character to the parameter value
                    state.AddToValue(char.ToLower(c));
                    state.NextCharLowerCase = false;
                }
                else
                {
                    // Add next character to the parameter value
                    state.AddToValue(c);
                }
            }
            else if (state.InExpression)
            {
                if (c == '{')
                {
                    // Starting inner expression
                    state.NumCurlyBraces++;
                }
                else if (c == '}')
                {
                    state.NumCurlyBraces--;
                    if (state.NumCurlyBraces == 0)
                    {
                        // Check if the round braces are properly terminated
                        if (state.NumRoundBraces > 0)
                        {
                            throw new CodeParserException("Unterminated round brace", result);
                        }
                        if (state.NumRoundBraces < 0)
                        {
                            throw new CodeParserException("Too many closing round braces", result);
                        }

                        // No longer in an expression
                        state.InExpression = false;
                        state.WasExpression = true;
                        state.EndingChunk = true;
                    }
                }
                else if (c == '(')
                {
                    // Starting inner expression
                    state.NumRoundBraces++;
                }
                else if (c == ')')
                {
                    // Ending inner expression
                    state.NumRoundBraces--;
                }
                state.AddToValue(c);
            }
            else if (c == ';')
            {
                state.InFinalComment = true;
                state.HadComment = true;
                state.InChunk = state.EndingChunk = false;
            }
            else if (c == '(')
            {
                state.InEncapsulatedComment = true;
                state.HadComment = true;
                state.InChunk = state.EndingChunk = false;
            }
            else if (!state.EndingChunk && state.ValueLength == 0)
            {
                if (char.IsWhiteSpace(c))
                {
                    // Parameter is empty
                    state.EndingChunk = true;
                }
                else if (c == '\'')
                {
                    // Parameter is a character
                    state.InSingleQuotes = true;
                    state.IsNumericParameter = false;
                }
                else if (c == '"')
                {
                    // Parameter is a quoted string
                    state.InDoubleQuotes = true;
                    state.IsNumericParameter = false;
                }
                else if (c == '{')
                {
                    // Parameter is an expression
                    state.SetValue("{");
                    state.InExpression = true;
                    state.IsNumericParameter = false;
                    state.NumCurlyBraces++;
                }
                else
                {
                    // Starting numeric or string parameter
                    state.IsNumericParameter = (c == ':' || NumericParameterChars.Contains(c)) && !state.UnprecedentedParameter;
                    state.AddToValue(c);
                }
            }
            else if (state.EndingChunk ||
                (state.UnprecedentedParameter && c == '\n') ||
                (!state.UnprecedentedParameter && char.IsWhiteSpace(c)) ||
                (state.IsNumericParameter && c != ':' && !NumericParameterChars.Contains(c)))
            {
                if ((c == '{' && state.ValueTrimEndEndsWithColon()) ||
                    (c == ':' && state.WasExpression))
                {
                    // Array expression, keep on reading
                    state.AddToValue(c);
                    state.InExpression = true;
                    state.IsNumericParameter = false;
                    if (c == '{')
                    {
                        state.NumCurlyBraces++;
                    }
                }
                else if ((c == 'e' || c == 'x') && !state.ValueContains(c))
                {
                    // Parameter contains special letter for hex or exp display
                    state.AddToValue(c);
                }
                else
                {
                    // Parameter has ended
                    state.InChunk = state.EndingChunk = false;
                }
            }
            else
            {
                // Reading more of the current chunk
                state.AddToValue(c);
            }

            if (state.EndingChunk && c == '\n')
            {
                // Last character - process the last parameter being read
                state.InChunk = state.EndingChunk = false;
            }
        }

        if (state.ReadingAtStart)
        {
            state.IsLineNumber = char.ToUpperInvariant(c) == 'N';
            if (char.IsWhiteSpace(c) && c != '\n')
            {
                if (c == '\t')
                {
                    int indent = (result.Indent + 4) & ~3;
                    if (indent >= byte.MaxValue)
                    {
                        throw new CodeParserException("Indentation too big", result);
                    }
                    result.Indent = (byte)indent;
                }
                else
                {
                    if (result.Indent == byte.MaxValue)
                    {
                        throw new CodeParserException("Indentation too big", result);
                    }
                    result.Indent++;
                }
            }
            else
            {
                state.ReadingAtStart = false;
            }
        }

        if (!state.InKeywordArgument && !state.InChunk && !state.ReadingAtStart)
        {
            if (state.Letter != '\0' || state.ValueLength > 0 || state.WasQuoted)
            {
                // Chunk is complete
                string value = state.GetValue();
                if (state.IsLineNumber)
                {
                    // Process line number
                    if (long.TryParse(value, out long lineNumber))
                    {
                        result.LineNumber = lineNumber;
                        result.Flags |= CodeFlags.HasExplicitLineNumber;
                    }
                    state.IsLineNumber = false;
                    state.HadLineNumber = true;
                }
                else if (((state.Letter == 'G' && value != "lobal") || state.Letter == 'M' || state.Letter == 'T') &&
                            (result.MajorNumber is null || (result.Type == CodeType.GCode && result.MajorNumber == 53)))
                {
                    // Process G/M/T identifier(s)
                    if (result.Type == CodeType.GCode && result.MajorNumber == 53)
                    {
                        result.MajorNumber = null;
                        result.Flags |= CodeFlags.EnforceAbsolutePosition;
                    }

                    result.Type = (CodeType)state.Letter;
                    if (state.WasExpression)
                    {
                        if (result.Type == CodeType.TCode)
                        {
                            AddParameter(result, 'T', value, false, true);
                        }
                        else
                        {
                            throw new CodeParserException("Dynamic command numbers are only supported for T-codes", result);
                        }
                    }
                    else if (value.Contains('.'))
                    {
                        int dotIndex = value.IndexOf('.');
                        string majorValue = value.Substring(0, dotIndex);
                        if (int.TryParse(majorValue, out int majorNumber))
                        {
                            result.MajorNumber = majorNumber;
                            // Codes with unprecedented parameters are not dot-separated
                        }
                        else
                        {
                            throw new CodeParserException($"Failed to parse major {char.ToUpperInvariant((char)result.Type)}-code number ({majorValue})", result);
                        }
                        // The minor version is a single fraction digit (0-9) as supported by the firmware
                        if (dotIndex + 1 < value.Length && char.IsDigit(value[dotIndex + 1]))
                        {
                            result.MinorNumber = value[dotIndex + 1] - '0';
                        }
                        else
                        {
                            throw new CodeParserException($"Failed to parse minor {char.ToUpperInvariant((char)result.Type)}-code number ({value.Substring(dotIndex + 1)})", result);
                        }
                    }
                    else if (int.TryParse(value, out int majorNumber))
                    {
                        result.MajorNumber = majorNumber;
                        state.UnprecedentedParameter = (state.Letter == 'M') && (majorNumber == 23 || majorNumber == 28 || majorNumber == 30 || majorNumber == 32 || majorNumber == 36 || majorNumber == 117);
                    }
                    else if (!string.IsNullOrWhiteSpace(value) || result.Type != CodeType.TCode)
                    {
                        throw new CodeParserException($"Failed to parse major {char.ToUpperInvariant((char)result.Type)}-code number ({value})", result);
                    }
                }
                else if (result.Type == CodeType.None && result.MajorNumber is null && !state.WasQuoted && !state.WasExpression)
                {
                    // Check for conditional G-code
                    string keyword = char.ToLowerInvariant(state.Letter) + value;
                    if (keyword == "if")
                    {
                        result.Type = CodeType.Keyword;
                        result.Keyword = KeywordType.If;
                        result.KeywordArgument = string.Empty;
                        state.InKeywordArgument = true;
                    }
                    else if (keyword == "elif")
                    {
                        result.Type = CodeType.Keyword;
                        result.Keyword = KeywordType.ElseIf;
                        result.KeywordArgument = string.Empty;
                        state.InKeywordArgument = true;
                    }
                    else if (keyword == "else")
                    {
                        result.Type = CodeType.Keyword;
                        result.Keyword = KeywordType.Else;
                    }
                    else if (keyword == "while")
                    {
                        result.Type = CodeType.Keyword;
                        result.Keyword = KeywordType.While;
                        result.KeywordArgument = string.Empty;
                        state.InKeywordArgument = true;
                    }
                    else if (keyword == "break")
                    {
                        result.Type = CodeType.Keyword;
                        result.Keyword = KeywordType.Break;
                    }
                    else if (keyword == "continue")
                    {
                        result.Type = CodeType.Keyword;
                        result.Keyword = KeywordType.Continue;
                    }
                    else if (keyword == "abort")
                    {
                        result.Type = CodeType.Keyword;
                        result.Keyword = KeywordType.Abort;
                        state.InKeywordArgument = true;
                    }
                    else if (keyword == "var")
                    {
                        result.Type = CodeType.Keyword;
                        result.Keyword = KeywordType.Var;
                        result.KeywordArgument = string.Empty;
                        state.InKeywordArgument = true;
                    }
                    else if (keyword == "global")
                    {
                        result.Type = CodeType.Keyword;
                        result.Keyword = KeywordType.Global;
                        result.KeywordArgument = string.Empty;
                        state.InKeywordArgument = true;
                    }
                    else if (keyword == "set")
                    {
                        result.Type = CodeType.Keyword;
                        result.Keyword = KeywordType.Set;
                        result.KeywordArgument = string.Empty;
                        state.InKeywordArgument = true;
                    }
                    else if (keyword == "echo")
                    {
                        result.Type = CodeType.Keyword;
                        result.Keyword = KeywordType.Echo;
                        result.KeywordArgument = string.Empty;
                        state.InKeywordArgument = true;
                    }
                    else if (keyword == "skip")
                    {
                        result.Type = CodeType.Keyword;
                        result.Keyword = KeywordType.Skip;
                    }
                    else if (!result.HasParameter(state.Letter))
                    {
                        AddParameter(result, state.Letter, value, false, state.MayRepeatCode || state.UnprecedentedParameter || state.IsNumericParameter);
                    }
                    // Ignore duplicate parameters
                }
                else
                {
                    if (state.Letter == '\0')
                    {
                        state.Letter = '@';
                    }
                    else if (state.UnprecedentedParameter)
                    {
                        value = state.Letter + value;
                        state.Letter = '@';
                    }

                    if (!result.HasParameter(state.Letter))
                    {
                        if (state.WasExpression && (!value.StartsWith("{", StringComparison.Ordinal) || !value.EndsWith("}", StringComparison.Ordinal)))
                        {
                            value = '{' + value.Trim() + '}';
                        }
                        AddParameter(result, state.Letter, value, state.WasQuoted, state.UnprecedentedParameter || state.IsNumericParameter || state.WasExpression);
                    }
                    // Ignore duplicate parameters
                }

                state.Letter = '\0';
                state.ClearValue();
                state.WasQuoted = state.WasExpression = false;
            }

            if (c == ';')
            {
                // Starting final comment
                state.ContentRead = state.InFinalComment = state.HadComment = true;
            }
            else if (c == '(' && !state.InExpression)
            {
                if (state.InKeywordArgument)
                {
                    // No space between keyword and brace. This is not an encapsulated comment
                    state.InEncapsulatedComment = false;
                    state.AddToValue('(');
                    state.NumRoundBraces++;
                }
                else
                {
                    // Starting encapsulated comment
                    state.ContentRead = state.InEncapsulatedComment = state.HadComment = true;
                }
            }
            else if (c == '\'')
            {
                state.ContentRead = state.NextCharLowerCase = true;
            }
            else if (!char.IsWhiteSpace(c))
            {
                // Starting a new parameter
                state.ContentRead = state.InChunk = true;
                if (c == '{')
                {
                    state.SetValue("{");
                    state.InExpression = true;
                    state.InSingleQuotes = state.InDoubleQuotes = false;
                    state.NumCurlyBraces++;
                }
                else if (c == '\'')
                {
                    state.InSingleQuotes = true;
                }
                else if (c == '"')
                {
                    state.InDoubleQuotes = true;
                }
                else if (state.NextCharLowerCase)
                {
                    state.Letter = char.ToLowerInvariant(c);
                    state.NextCharLowerCase = false;
                }
                else if (!state.UnprecedentedParameter)
                {
                    state.Letter = char.ToUpperInvariant(c);
                }
                else
                {
                    state.Letter = c;
                }
            }
        }

        return consumedPeek;
    }

    /// <summary>
    /// Check if the upcoming character starts another G/M/T-code while the current one is complete
    /// </summary>
    private static bool StartsNextCode(ParserState state, Code result, char c)
    {
        if (!state.ContentRead || state.InFinalComment || state.InEncapsulatedComment || state.InKeywordArgument || state.InChunk)
        {
            return false;
        }

        char nextChar = state.NextCharLowerCase ? c : char.ToUpperInvariant(c);
        return (nextChar == 'G' || nextChar == 'M' || nextChar == 'T') && result.Type != CodeType.None &&
            (result.Type != CodeType.GCode || result.MajorNumber != 53) &&
            (nextChar != 'T' || result.Type == CodeType.TCode || result.Parameters.Any(item => item.Letter == 'T'));
    }

    /// <summary>
    /// Finalize a parsed code after the input line or stream ended
    /// </summary>
    /// <exception cref="CodeParserException">Thrown if the code is malformed</exception>
    private static void FinishCode(ParserState state, Code result, char lastChar)
    {
        // Check if this was the last code on the line
        if (lastChar is '\n' or '\0')
        {
            result.Flags |= CodeFlags.IsLastCode;
        }

        // Materialize the comment that was read for this code
        result.Comment = state.GetComment();

        // Check if this is a whole-line comment
        if (result.Type == CodeType.None && result.Parameters.Count == 0 && result.Comment is not null)
        {
            result.Type = CodeType.Comment;
        }

        // Do not allow malformed codes
        if (state.InEncapsulatedComment)
        {
            throw new CodeParserException("Unterminated encapsulated comment", result);
        }
        if (state.InSingleQuotes)
        {
            throw new CodeParserException("Unterminated character literal", result);
        }
        if (state.InDoubleQuotes)
        {
            throw new CodeParserException("Unterminated string", result);
        }
        if (state.NumCurlyBraces > 0)
        {
            throw new CodeParserException("Unterminated curly brace", result);
        }
        if (state.NumCurlyBraces < 0)
        {
            throw new CodeParserException("Too many closing curly braces", result);
        }
        if (state.InKeywordArgument)
        {
            result.KeywordArgument = state.GetValue().Trim();
        }
        if (result.KeywordArgument?.Length > 255)
        {
            throw new CodeParserException("Keyword argument too long (> 255)", result);
        }
        if (result.Parameters.Count > 255)
        {
            throw new CodeParserException("Too many parameters (> 255)", result);
        }

        // M569, M584, and M915 use driver identifiers
        result.ConvertDriverIds();
    }

    /// <summary>
    /// Parse the next available G/M/T-code from the given stream
    /// </summary>
    /// <param name="reader">Input to read from</param>
    /// <param name="result">Code to fill</param>
    /// <returns>Whether anything could be read</returns>
    /// <exception cref="CodeParserException">Thrown if the code contains errors like unterminated strings or unterminated comments</exception>
    /// <remarks>
    /// In general it is better to use the asynchronous ParseAsync method because this implementation
    /// - does not update the line number unless it is specified using the 'N' character
    /// - does not set the corresponding flag for G53 after the first code on a line
    /// - sets the indentation level only for the first code in a line
    /// - does not support Fanuc or LaserWeb styles
    /// </remarks>
    public static bool Parse(TextReader reader, Code result)
    {
        ParserState state = new() { ReadingAtStart = true };
        result.Length = 0;

        // The shared parser works on UTF-8 bytes. Decode characters from the reader and re-encode
        // them so that multi-byte content round-trips correctly regardless of the source encoding.
        byte[] pending = new byte[4];
        char[] charBuffer = new char[2];
        int pendingLength = 0, pendingPointer = 0;

        int ReadByte()
        {
            if (pendingPointer >= pendingLength)
            {
                int next = reader.Read();
                if (next < 0)
                {
                    return -1;
                }

                char ch = (char)next;
                if (char.IsHighSurrogate(ch) && reader.Peek() >= 0)
                {
                    charBuffer[0] = ch;
                    charBuffer[1] = (char)reader.Read();
                    pendingLength = Encoding.UTF8.GetBytes(charBuffer, 0, 2, pending, 0);
                }
                else
                {
                    charBuffer[0] = ch;
                    pendingLength = Encoding.UTF8.GetBytes(charBuffer, 0, 1, pending, 0);
                }
                pendingPointer = 0;
            }
            return pending[pendingPointer++];
        }

        char PeekChar()
        {
            if (pendingPointer < pendingLength)
            {
                return (char)pending[pendingPointer];
            }

            int next = reader.Peek();
            if (next < 0)
            {
                return '\0';
            }
            // Lookahead is only ever compared against ASCII characters, so a non-ASCII byte is irrelevant
            return (next < 0x80) ? (char)next : '\xFF';
        }

        char c;
        do
        {
            int b = ReadByte();
            c = (b < 0) ? '\n' : (char)b;
            result.Length++;

            if (c == '\r')
            {
                // Ignore CR
                continue;
            }

            // Stop if another G/M/T code is coming up and this one is complete
            if (StartsNextCode(state, result, c))
            {
                // The character belongs to the next code, so put it back and do not count it
                pendingPointer--;
                result.Length--;
                break;
            }

            if (ProcessCharacter(state, result, c, PeekChar()))
            {
                ReadByte();
                result.Length++;
            }
        }
        while (c != '\n');

        FinishCode(state, result, c);
        return state.ContentRead;
    }

    /// <summary>
    /// Add a new parameter to a given <see cref="Code"/> instance
    /// </summary>
    /// <param name="code">Code to add the parameter to</param>
    /// <param name="letter">Letter of the parameter to 0 if unprecedented</param>
    /// <param name="value">Value of the parameter</param>
    /// <param name="isQuoted">Whether the parameter is a quoted string</param>
    /// <param name="isSingleParameter">Whether the parameter is definitely a single parameter</param>
    private static void AddParameter(Code code, char letter, string value, bool isQuoted, bool isSingleParameter)
    {
        if (letter != '@' && !char.IsLetter(letter))
        {
            throw new CodeParserException($"Illegal parameter letter '{letter}'");
        }

        if (isQuoted || isSingleParameter)
        {
            // Standard parameter
            code.Parameters.Add(new CodeParameter(letter, value, isQuoted, false));
        }
        else
        {
            // Parameters like "XYZ" in M84 XYZ
            code.Parameters.Add(new CodeParameter(letter, string.Empty, false, false));
            foreach (char c in value)
            {
                if (c == '"')
                {
                    throw new CodeParserException("Unterminated string", code);
                }
                if (c != '@' && !char.IsLetter(c))
                {
                    throw new CodeParserException($"Illegal parameter letter '{c}'");
                }
                code.Parameters.Add(new CodeParameter(c, string.Empty, false, false));
            }
        }
    }

    /// <summary>
    /// Convert parameters of this code to driver id(s)
    /// </summary>
    /// <exception cref="CodeParserException">Driver ID could not be parsed</exception>
    public void ConvertDriverIds()
    {
        if (Type == CodeType.MCode)
        {
            switch (MajorNumber)
            {
                case 569:
                case 915:
                case 955:
                case 956:
                    foreach (CodeParameter parameter in Parameters)
                    {
                        if (!parameter.IsExpression && char.ToUpperInvariant(parameter.Letter) == 'P')
                        {
                            ConvertDriverIds(parameter);
                        }
                    }
                    break;

                case 584:
                    foreach (CodeParameter parameter in Parameters)
                    {
                        if (!parameter.IsExpression && (ObjectModel.Axis.Letters.Contains(parameter.Letter) || char.ToUpperInvariant(parameter.Letter) == 'E'))
                        {
                            ConvertDriverIds(parameter);
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Convert a given parameter to driver id(s)
    /// </summary>
    /// <exception cref="CodeParserException">Driver ID could not be parsed</exception>
    private void ConvertDriverIds(CodeParameter parameter)
    {
        if (!parameter.IsExpression)
        {
            List<DriverId> drivers = [];

            string[] parameters = parameter.StringValue.Split(':') ?? [];
            foreach (string value in parameters)
            {
                try
                {
                    DriverId id = new(value);
                    drivers.Add(id);
                }
                catch (ArgumentException e)
                {
                    throw new CodeParserException(e.Message + $" from {parameter.Letter} parameter", this);
                }
            }

            if (drivers.Count == 1)
            {
                parameter.ParsedValue = drivers[0];
            }
            else
            {
                parameter.ParsedValue = drivers.ToArray();
            }
            parameter.IsDriverId = true;
        }
    }
}
