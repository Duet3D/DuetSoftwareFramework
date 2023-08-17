using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DuetAPI.Commands
{
    public partial class Code
    {
        /// <summary>
        /// Parse the next available G/M/T-code from the given stream reader asynchronously
        /// </summary>
        /// <param name="reader">Stream reader to read from</param>
        /// <param name="result">Code to fill</param>
        /// <param name="buffer">Internal buffer for parsing codes</param>
        /// <returns>Whether anything could be read</returns>
        /// <exception cref="CodeParserException">Thrown if the code contains errors like unterminated strings or unterminated comments</exception>
        [Obsolete("This call is deprecated because the buffer position of a StreamReader is not accessible. Pass your stream directly instead")]
        public static ValueTask<bool> ParseAsync(StreamReader reader, Code result, CodeParserBuffer buffer) => ParseAsync(reader.BaseStream, result, buffer);

        /// <summary>
        /// Parse the next available G/M/T-code from the given stream asynchronously
        /// </summary>
        /// <param name="stream">Stream to read from</param>
        /// <param name="result">Code to fill</param>
        /// <param name="buffer">Internal buffer for parsing codes</param>
        /// <returns>Whether anything could be read</returns>
        /// <exception cref="ArgumentException">BOM from start of file showed that this file is neither ASCII nor UTF-8</exception>
        /// <exception cref="CodeParserException">Thrown if the code contains errors like unterminated strings or unterminated comments</exception>
        public static async ValueTask<bool> ParseAsync(Stream stream, Code result, CodeParserBuffer buffer)
        {
            // Deal with BOM when starting to parse a file. Previously this was done by the used StreamReader instance
            if (buffer.IsFile && stream.Position + buffer.Pointer == 0)
            {
                buffer.Size = await stream.ReadAsync(buffer.Content, 0, buffer.Content.Length);
                buffer.Pointer = 0;

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
            char letter = '\0', lastC, c = '\0';
            string value = string.Empty;

            bool contentRead = false, unprecedentedParameter = false;
            bool inFinalComment = false, inEncapsulatedComment = false, inChunk = false, inSingleQuotes = false, inDoubleQuotes = false, inExpression = false, inCondition = false;
            bool readingAtStart = buffer.SeenNewLine, isLineNumber = false, hadLineNumber = false, isNumericParameter = false, endingChunk = false;
            bool nextCharLowerCase = false, wasQuoted = false, wasExpression = false;
            int numCurlyBraces = 0, numRoundBraces = 0;
            buffer.SeenNewLine = false;

            result.Flags = buffer.EnforcingAbsolutePosition ? CodeFlags.EnforceAbsolutePosition : CodeFlags.None;
            result.Indent = buffer.Indent;
            result.Length = 0;
            result.FilePosition = buffer.IsFile ? buffer.GetPosition(stream) : null;
            result.LineNumber = buffer.LineNumber;

            do
            {
                // Check if the buffer needs to be filled
                if (buffer.Pointer >= buffer.Size)
                {
                    buffer.Size = await stream.ReadAsync(buffer.Content, 0, buffer.Content.Length);
                    buffer.Pointer = 0;
                }

                // Read the next character
                lastC = c;
                c = (buffer.Pointer < buffer.Size) ? (char)buffer.Content[buffer.Pointer] : '\n';
                result.Length++;
                buffer.Pointer++;

                if (c == '\n' && !hadLineNumber && buffer.LineNumber is not null)
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
                if (contentRead && !inFinalComment && !inEncapsulatedComment && !inCondition && !inChunk)
                {
                    char nextChar = char.ToUpperInvariant(c);
                    if ((nextChar == 'G' || nextChar == 'M' || nextChar == 'T') && result.Type != CodeType.None &&
                        (result.Type != CodeType.GCode || result.MajorNumber != 53) &&
                        (nextChar != 'T' || result.Type == CodeType.TCode || result.Parameters.Any(item => item.Letter == 'T')))
                    {
                        // Note that M-codes may have G or T parameters but only one
                        buffer.Pointer--;
                        break;
                    }
                }

                if (inFinalComment)
                {
                    // Reading a comment ending the current line
                    if (c != '\n')
                    {
                        // Add next character to the comment unless it is the "artificial" 0-character termination
                        result.Comment += c;
                    }
                    else
                    {
                        // Something started a comment, so the comment cannot be null any more
                        result.Comment ??= string.Empty;
                    }
                    continue;
                }

                if (inEncapsulatedComment)
                {
                    // Reading an encapsulated comment in braces
                    if (c != ')')
                    {
                        // Add next character to the comment
                        result.Comment += c;
                    }
                    else
                    {
                        // End of encapsulated comment, it cannot be null any more
                        result.Comment ??= string.Empty;
                        inEncapsulatedComment = false;
                    }
                    continue;
                }

                if (inCondition)
                {
                    if (inSingleQuotes)
                    {
                        // Add next character to the parameter value
                        result.KeywordArgument += c;

                        if (c == '\'')
                        {
                            if (buffer.Pointer >= buffer.Size)
                            {
                                buffer.Size = await stream.ReadAsync(buffer.Content, 0, buffer.Content.Length);
                                buffer.Pointer = 0;
                            }

                            char nextC = (buffer.Pointer < buffer.Size) ? (char)buffer.Content[buffer.Pointer] : '\0';
                            if (nextC == '\'')
                            {
                                // Subsequent single quotes are treated as a single quote char
                                result.KeywordArgument += c;
                                buffer.Pointer++;
                                result.Length++;
                            }
                            inSingleQuotes = false;
                        }
                    }
                    else if (inDoubleQuotes)
                    {
                        // Add next character to the parameter value
                        result.KeywordArgument += c;

                        if (c == '"')
                        {
                            if (buffer.Pointer >= buffer.Size)
                            {
                                buffer.Size = await stream.ReadAsync(buffer.Content, 0, buffer.Content.Length);
                                buffer.Pointer = 0;
                            }

                            char nextC = (buffer.Pointer < buffer.Size) ? (char)buffer.Content[buffer.Pointer] : '\0';
                            if (nextC == '"')
                            {
                                // Subsequent double quotes are treated as a single quote char
                                result.KeywordArgument += c;
                                buffer.Pointer++;
                                result.Length++;
                            }
                            else
                            {
                                // No longer in an escaped parameter
                                inDoubleQuotes = false;
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
                                result.KeywordArgument += '\'';
                                inSingleQuotes = true;
                                break;
                            case '"':
                                result.KeywordArgument += '"';
                                inDoubleQuotes = true;
                                break;
                            case ';':
                                inCondition = false;
                                inFinalComment = true;
                                break;
                            case '{':
                                result.KeywordArgument += '{';
                                numCurlyBraces++;
                                break;
                            case '}':
                                result.KeywordArgument += '}';
                                numCurlyBraces--;
                                break;
                            case '(':
                                result.KeywordArgument += '(';
                                numRoundBraces++;
                                break;
                            case ')':
                                if (numRoundBraces > 0)
                                {
                                    result.KeywordArgument += ')';
                                    numRoundBraces--;
                                }
                                else
                                {
                                    throw new CodeParserException("Unexpected closing round brace", result);
                                }
                                break;
                            default:
                                if (!char.IsWhiteSpace(c) || !string.IsNullOrEmpty(result.KeywordArgument))
                                {
                                    // In fact, it should be possible to leave out whitespaces here but we here don't check for quoted strings yet
                                    result.KeywordArgument += c;
                                }
                                break;
                        }
                    }

                    if (inCondition)
                    {
                        continue;
                    }
                }

                if (inChunk)
                {
                    if (inSingleQuotes)
                    {
                        if (c == '\'')
                        {
                            if (buffer.Pointer >= buffer.Size)
                            {
                                buffer.Size = await stream.ReadAsync(buffer.Content, 0, buffer.Content.Length);
                                buffer.Pointer = 0;
                            }

                            char nextC = (buffer.Pointer < buffer.Size) ? (char)buffer.Content[buffer.Pointer] : '\0';
                            if (nextC == '\'')
                            {
                                // Treat subsequent single quotes as a single double-quote char
                                value += '"';
                                buffer.Pointer++;
                                result.Length++;
                            }
                            inSingleQuotes = false;
                            wasQuoted = true;
                            endingChunk = true;
                        }
                        else
                        {
                            // Add next character to the parameter value
                            value += c;
                        }
                    }
                    else if (inDoubleQuotes)
                    {
                        if (c == '\'')
                        {
                            if (nextCharLowerCase)
                            {
                                // Treat subsequent single-quotes as a single-quite char
                                value += '\'';
                                nextCharLowerCase = false;
                            }
                            else
                            {
                                // Next letter should be lower-case
                                nextCharLowerCase = true;
                            }
                        }
                        else if (c == '"')
                        {
                            if (buffer.Pointer >= buffer.Size)
                            {
                                buffer.Size = await stream.ReadAsync(buffer.Content, 0, buffer.Content.Length);
                                buffer.Pointer = 0;
                            }

                            char nextC = (buffer.Pointer < buffer.Size) ? (char)buffer.Content[buffer.Pointer] : '\0';
                            if (nextC == '"')
                            {
                                // Treat subsequent double quotes as a single double-quote char
                                value += '"';
                                buffer.Pointer++;
                                result.Length++;
                            }
                            else
                            {
                                // No longer in an escaped parameter
                                inDoubleQuotes = nextCharLowerCase = false;
                                wasQuoted = true;
                                endingChunk = true;
                            }
                        }
                        else if (nextCharLowerCase)
                        {
                            // Add next lower-case character to the parameter value
                            value += char.ToLower(c);
                            nextCharLowerCase = false;
                        }
                        else
                        {
                            // Add next character to the parameter value
                            value += c;
                        }
                    }
                    else if (inExpression)
                    {
                        if (c == '{')
                        {
                            // Starting inner expression
                            numCurlyBraces++;
                        }
                        else if (c == '}')
                        {
                            numCurlyBraces--;
                            if (numCurlyBraces == 0)
                            {
                                // Check if the round braces are properly terminated
                                if (numRoundBraces > 0)
                                {
                                    throw new CodeParserException("Unterminated round brace", result);
                                }
                                if (numRoundBraces < 0)
                                {
                                    throw new CodeParserException("Too many closing round braces", result);
                                }

                                // No longer in an expression
                                inExpression = false;
                                wasExpression = true;
                                endingChunk = true;
                            }
                        }
                        else if (c == '(')
                        {
                            // Starting inner expression
                            numRoundBraces++;
                        }
                        else if (c == ')')
                        {
                            // Ending inner expression
                            numRoundBraces--;
                        }
                        value += c;
                    }
                    else if (c == ';')
                    {
                        inFinalComment = true;
                        inChunk = endingChunk = false;
                    }
                    else if (c == '(')
                    {
                        inEncapsulatedComment = true;
                        inChunk = endingChunk = false;
                    }
                    else if (!endingChunk && string.IsNullOrEmpty(value))
                    {
                        if (char.IsWhiteSpace(c))
                        {
                            // Parameter is empty
                            endingChunk = true;
                        }
                        else if (c == '\'')
                        {
                            // Parameter is a character
                            inSingleQuotes = true;
                            isNumericParameter = false;
                        }
                        else if (c == '"')
                        {
                            // Parameter is a quoted string
                            inDoubleQuotes = true;
                            isNumericParameter = false;
                        }
                        else if (c == '{')
                        {
                            // Parameter is an expression
                            value = "{";
                            inExpression = true;
                            isNumericParameter = false;
                            numCurlyBraces++;
                        }
                        else
                        {
                            // Starting numeric or string parameter
                            isNumericParameter = (c == ':' || NumericParameterChars.Contains(c)) && !unprecedentedParameter;
                            value += c;
                        }
                    }
                    else if (endingChunk ||
                        (unprecedentedParameter && c == '\n') ||
                        (!unprecedentedParameter && char.IsWhiteSpace(c)) ||
                        (isNumericParameter && c != ':' && !NumericParameterChars.Contains(c)))
                    {
                        if ((c == '{' && value.TrimEnd().EndsWith(":")) ||
                            (c == ':' && wasExpression))
                        {
                            // Array expression, keep on reading
                            value += c;
                            inExpression = true;
                            isNumericParameter = false;
                            if (c == '{')
                            {
                                numCurlyBraces++;
                            }
                        }
                        else if ((c == 'e' || c == 'x') && !value.Contains(c))
                        {
                            // Parameter contains special letter for hex or exp display
                            value += c;
                        }
                        else
                        {
                            // Parameter has ended
                            inChunk = endingChunk = false;
                        }
                    }
                    else
                    {
                        // Reading more of the current chunk
                        value += c;
                    }

                    if (endingChunk && c == '\n')
                    {
                        // Last character - process the last parameter being read
                        inChunk = endingChunk = false;
                    }
                }

                if (readingAtStart)
                {
                    isLineNumber = (char.ToUpperInvariant(c) == 'N');
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
                            buffer.Indent = (byte)indent;
                        }
                        else
                        {
                            if (result.Indent == byte.MaxValue)
                            {
                                throw new CodeParserException("Indentation too big", result);
                            }
                            result.Indent++;
                            buffer.Indent++;
                        }
                    }
                    else
                    {
                        readingAtStart = false;
                    }
                }

                if (!inCondition && !inChunk && !readingAtStart)
                {
                    if (letter != '\0' || !string.IsNullOrEmpty(value) || wasQuoted)
                    {
                        // Chunk is complete
                        if (isLineNumber)
                        {
                            // Process line number
                            if (long.TryParse(value, out long lineNumber))
                            {
                                result.LineNumber = lineNumber;
                                buffer.LineNumber = lineNumber;
                            }
                            isLineNumber = false;
                            hadLineNumber = true;
                        }
                        else if (((letter == 'G' && value != "lobal") || letter == 'M' || letter == 'T') &&
                                 (result.MajorNumber is null || (result.Type == CodeType.GCode && result.MajorNumber == 53)))
                        {
                            // Process G/M/T identifier(s)
                            if (result.Type == CodeType.GCode && result.MajorNumber == 53)
                            {
                                result.MajorNumber = null;
                                result.Flags |= CodeFlags.EnforceAbsolutePosition;
                                buffer.EnforcingAbsolutePosition = true;
                            }

                            result.Type = (CodeType)letter;
                            if (wasExpression)
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
                                string[] args = value.Split('.');
                                if (int.TryParse(args[0], out int majorNumber))
                                {
                                    result.MajorNumber = majorNumber;
                                    // Codes with unprecedented parameters are not dot-separated
                                }
                                else
                                {
                                    throw new CodeParserException($"Failed to parse major {char.ToUpperInvariant((char)result.Type)}-code number ({args[0]})", result);
                                }
                                if (sbyte.TryParse(args[1], out sbyte minorNumber) && minorNumber >= 0)
                                {
                                    result.MinorNumber = minorNumber;
                                }
                                else
                                {
                                    throw new CodeParserException($"Failed to parse minor {char.ToUpperInvariant((char)result.Type)}-code number ({args[1]})", result);
                                }
                            }
                            else if (int.TryParse(value, out int majorNumber))
                            {
                                result.MajorNumber = majorNumber;
                                unprecedentedParameter = (letter == 'M') && (majorNumber == 23 || majorNumber == 28 || majorNumber == 30 || majorNumber == 32 || majorNumber == 36 || majorNumber == 117);
                            }
                            else if (!string.IsNullOrWhiteSpace(value) || result.Type != CodeType.TCode)
                            {
                                throw new CodeParserException($"Failed to parse major {char.ToUpperInvariant((char)result.Type)}-code number ({value})", result);
                            }
                        }
                        else if (result.Type == CodeType.None && result.MajorNumber is null && !wasQuoted && !wasExpression)
                        {
                            // Check for conditional G-code
                            string keyword = char.ToLowerInvariant(letter) + value;
                            if (keyword == "if")
                            {
                                result.Type = CodeType.Keyword;
                                result.Keyword = KeywordType.If;
                                result.KeywordArgument = string.Empty;
                                inCondition = true;
                            }
                            else if (keyword == "elif")
                            {
                                result.Type = CodeType.Keyword;
                                result.Keyword = KeywordType.ElseIf;
                                result.KeywordArgument = string.Empty;
                                inCondition = true;
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
                                inCondition = true;
                            }
                            else if (keyword == "break")
                            {
                                result.Type = CodeType.Keyword;
                                result.Keyword = KeywordType.Break;
                                inCondition = true;
                            }
                            else if (keyword == "continue")
                            {
                                result.Type = CodeType.Keyword;
                                result.Keyword = KeywordType.Continue;
                                inCondition = true;
                            }
                            else if (keyword == "abort")
                            {
                                result.Type = CodeType.Keyword;
                                result.Keyword = KeywordType.Abort;
                                inCondition = true;
                            }
                            else if (keyword == "var")
                            {
                                result.Type = CodeType.Keyword;
                                result.Keyword = KeywordType.Var;
                                result.KeywordArgument = string.Empty;
                                inCondition = true;
                            }
                            else if (keyword == "global")
                            {
                                result.Type = CodeType.Keyword;
                                result.Keyword = KeywordType.Global;
                                result.KeywordArgument = string.Empty;
                                inCondition = true;
                            }
                            else if (keyword == "set")
                            {
                                result.Type = CodeType.Keyword;
                                result.Keyword = KeywordType.Set;
                                result.KeywordArgument = string.Empty;
                                inCondition = true;
                            }
                            else if (keyword == "echo")
                            {
                                result.Type = CodeType.Keyword;
                                result.Keyword = KeywordType.Echo;
                                result.KeywordArgument = string.Empty;
                                inCondition = true;
                            }
#warning do not permit duplicate parameters in v3.6
#if false
                            else if (!result.HasParameter(letter))
#else
                            else
#endif
                            {
                                AddParameter(result, letter, value, false, buffer.MayRepeatCode || unprecedentedParameter || isNumericParameter);
                            }
                            // Ignore duplicate parameters
                        }
                        else
                        {
                            if (letter == '\0')
                            {
                                letter = '@';
                            }
                            else if (unprecedentedParameter)
                            {
                                value = letter + value;
                                letter = '@';
                            }

#warning do not permit duplicate parameters in v3.6
#if false
                            if (!result.HasParameter(letter))
#endif
                            {
                                if (wasExpression && (!value.StartsWith("{") || !value.EndsWith("}")))
                                {
                                    value = '{' + value.Trim() + '}';
                                }
                                AddParameter(result, letter, value, wasQuoted, unprecedentedParameter || isNumericParameter || wasExpression);
                            }
                            // Ignore duplicate parameters
                        }

                        letter = '\0';
                        value = string.Empty;
                        wasQuoted = wasExpression = false;
                    }

                    if (c == ';')
                    {
                        // Starting final comment
                        contentRead = inFinalComment = true;
                    }
                    else if (c == '(' && !inExpression)
                    {
                        // Starting encapsulated comment
                        contentRead = inEncapsulatedComment = true;
                    }
                    else if (c == '\'')
                    {
                        contentRead = nextCharLowerCase = true;
                    }
                    else if (!char.IsWhiteSpace(c))
                    {
                        // Starting a new parameter
                        contentRead = inChunk = true;
                        if (c == '{')
                        {
                            value = "{";
                            inExpression = true;
                            inSingleQuotes = inDoubleQuotes = false;
                            numCurlyBraces++;
                        }
                        else if (c == '\'')
                        {
                            inSingleQuotes = true;
                        }
                        else if (c == '"')
                        {
                            inDoubleQuotes = true;
                        }
                        else if (nextCharLowerCase)
                        {
                            letter = char.ToLowerInvariant(c);
                            nextCharLowerCase = false;
                        }
                        else if (!unprecedentedParameter)
                        {
                            letter = char.ToUpperInvariant(c);
                        }
                        else
                        {
                            letter = c;
                        }
                    }
                }
            } while (c != '\n');

            // Check if this was the last code on the line and if the state can be reset
            if (c == '\n')
            {
                result.Flags |= CodeFlags.IsLastCode;
                buffer.InvalidateData();
            }

            // Deal with Fanuc and LaserWeb G-code styles
            if (buffer.MayRepeatCode)
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

            // Check if this is a whole-line comment
            if (result.Type == CodeType.None && result.Parameters.Count == 0 && result.Comment is not null)
            {
                result.Type = CodeType.Comment;
            }

            // Do not allow malformed codes
            if (inEncapsulatedComment)
            {
                throw new CodeParserException("Unterminated encapsulated comment", result);
            }
            if (inSingleQuotes)
            {
                throw new CodeParserException("Unterminated character literal", result);
            }
            if (inDoubleQuotes)
            {
                throw new CodeParserException("Unterminated string", result);
            }
            if (numCurlyBraces > 0)
            {
                throw new CodeParserException("Unterminated curly brace", result);
            }
            if (numCurlyBraces < 0)
            {
                throw new CodeParserException("Too many closing curly braces", result);
            }
            if (result.KeywordArgument is not null)
            {
                result.KeywordArgument = result.KeywordArgument.Trim();
                if (result.KeywordArgument.Length > 255)
                {
                    throw new CodeParserException("Keyword argument too long (> 255)", result);
                }
            }
            if (result.Parameters.Count > 255)
            {
                throw new CodeParserException("Too many parameters (> 255)", result);
            }

            // M569, M584, and M915 use driver identifiers
            result.ConvertDriverIds();

            // End
            return contentRead;
        }
    }
}
