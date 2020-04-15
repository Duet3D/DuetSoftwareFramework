using System.IO;
using System.Linq;
using System.Text;

namespace DuetAPI.Commands
{
    public partial class Code
    {
        // Numeric parameters may hold only characters of this string 
        private const string NumericParameterChars = "01234567890+-.e";

        /// <summary>
        /// Parse the next available G/M/T-code from the given stream
        /// </summary>
        /// <param name="reader">Input to read from</param>
        /// <param name="result">Code to fill</param>
        /// <param name="seenNewLine">If this is the first code or a NL character has been parsed</param>
        /// <returns>Whether anything could be read</returns>
        /// <exception cref="CodeParserException">Thrown if the code contains errors like unterminated strings or unterminated comments</exception>
        public static bool Parse(TextReader reader, Code result, ref bool seenNewLine)
        {
            char letter = '\0', c;
            string value = string.Empty;

            bool contentRead = false, unprecedentedParameter = false;
            bool inFinalComment = false, inEncapsulatedComment = false, inChunk = false, inQuotes = false, inExpression = false, inCondition = false;
            bool readingAtStart = seenNewLine, isLineNumber = false, hadLineNumber = false, isNumericParameter = false, endingChunk = false;
            bool wasQuoted = false, wasExpression = false;
            seenNewLine = false;

            Encoding encoding = (reader is StreamReader sr) ? sr.CurrentEncoding : Encoding.UTF8;
            result.Length = 0;
            do
            {
                // Read the next character
                int currentChar = reader.Read();
                c = (currentChar < 0) ? '\n' : (char)currentChar;
                result.Length += encoding.GetByteCount(new char[] { c });

                if (c == '\r')
                {
                    // Ignore CR
                    continue;
                }
                if (currentChar == '\n' && !hadLineNumber && result.LineNumber != null)
                {
                    // Keep track of the line number (if possible)
                    result.LineNumber++;
                }

                if (inFinalComment)
                {
                    // Reading a comment ending the current line
                    if (c != '\n')
                    {
                        // Add next character to the comment unless it is the "artificial" 0-character termination
                        result.Comment += c;
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
                        // Even though RepRapFirmware treats comments in braces differently,
                        // the correct approach should be to switch back to reading mode when the comment tag is closed
                        inEncapsulatedComment = false;
                    }
                    continue;
                }

                if (inCondition)
                {
                    switch (c)
                    {
                        case '\n':
                            // Ignore final NL
                            break;
                        case ';':
                            inCondition = false;
                            inFinalComment = true;
                            break;
                        case '(':
                            inCondition = false;
                            inEncapsulatedComment = true;
                            break;
                        default:
                            if (!char.IsWhiteSpace(c) || !string.IsNullOrEmpty(result.KeywordArgument))
                            {
                                // In fact, it should be possible to leave out whitespaces here but we here don't check for quoted strings yet
                                result.KeywordArgument += c;
                            }
                            break;
                    }

                    if (inCondition)
                    {
                        continue;
                    }
                }

                if (inChunk)
                {
                    if (inQuotes)
                    {
                        if (c == '"')
                        {
                            if (reader.Peek() == '"')
                            {
                                // Treat subsequent double quotes as a single quote char
                                value += '"';
                                reader.Read();
                                result.Length++;
                            }
                            else
                            {
                                // No longer in an escaped parameter
                                inQuotes = false;
                                wasQuoted = true;
                                endingChunk = true;
                            }
                        }
                        else
                        {
                            // Add next character to the parameter value
                            value += c;
                        }
                    }
                    else if (inExpression)
                    {
                        if (c == '}')
                        {
                            // No longer in an expression
                            inExpression = false;
                            wasExpression = true;
                            endingChunk = true;
                        }
                        value += c;
                    }
                    else if (!endingChunk && string.IsNullOrEmpty(value))
                    {
                        if (char.IsWhiteSpace(c))
                        {
                            // Parameter is empty
                            endingChunk = true;
                        }
                        else if (c == '"')
                        {
                            // Parameter is a quoted string
                            inQuotes = true;
                            isNumericParameter = false;
                        }
                        else if (c == '{')
                        {
                            // Parameter is an expression
                            value = "{";
                            inExpression = true;
                            isNumericParameter = false;
                        }
                        else
                        {
                            // Starting numeric or string parameter
                            isNumericParameter = (c != 'e') && (c == ':' || NumericParameterChars.Contains(c)) && !unprecedentedParameter;
                            value += c;
                        }
                    }
                    else if (endingChunk ||
                        (unprecedentedParameter && c == '\n') ||
                        (!unprecedentedParameter && char.IsWhiteSpace(c)) ||
                        (isNumericParameter && c != ':' && !NumericParameterChars.Contains(c)))
                    {
                        // Parameter has ended
                        inChunk = endingChunk = false;
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
                        if (result.Indent == byte.MaxValue)
                        {
                            throw new CodeParserException("Indentation too big", result);
                        }
                        result.Indent++;
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
                        char upperLetter = char.ToUpperInvariant(letter);
                        if (isLineNumber)
                        {
                            // Process line number
                            if (int.TryParse(value, out int lineNumber))
                            {
                                result.LineNumber = lineNumber;
                            }
                            isLineNumber = false;
                            hadLineNumber = true;
                        }
                        else if ((upperLetter == 'G' || upperLetter == 'M' || upperLetter == 'T') &&
                                 (result.MajorNumber == null || (result.Type == CodeType.GCode && result.MajorNumber == 53)))
                        {
                            // Process G/M/T identifier(s)
                            if (result.Type == CodeType.GCode && result.MajorNumber == 53)
                            {
                                result.MajorNumber = null;
                                result.Flags |= CodeFlags.EnforceAbsolutePosition;
                            }

                            result.Type = (CodeType)upperLetter;
                            if (value.Contains('.'))
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
                                unprecedentedParameter = (upperLetter == 'M') && (majorNumber == 23 || majorNumber == 28 || majorNumber == 30 || majorNumber == 32 || majorNumber == 36 || majorNumber == 117);
                            }
                            else if (!string.IsNullOrWhiteSpace(value) || result.Type != CodeType.TCode)
                            {
                                throw new CodeParserException($"Failed to parse major {char.ToUpperInvariant((char)result.Type)}-code number ({value})", result);
                            }
                        }
                        else if (result.MajorNumber == null && result.Keyword == KeywordType.None && !wasQuoted && !wasExpression)
                        {
                            // Check for conditional G-code
                            if (letter == 'i' && value == "f")
                            {
                                result.Keyword = KeywordType.If;
                                result.KeywordArgument = string.Empty;
                                inCondition = true;
                            }
                            else if (letter == 'e' && value == "lif")
                            {
                                result.Keyword = KeywordType.ElseIf;
                                result.KeywordArgument = string.Empty;
                                inCondition = true;
                            }
                            else if (letter == 'e' && value == "lse")
                            {
                                result.Keyword = KeywordType.Else;
                            }
                            else if (letter == 'w' && value == "hile")
                            {
                                result.Keyword = KeywordType.While;
                                result.KeywordArgument = string.Empty;
                                inCondition = true;
                            }
                            else if (letter == 'b' && value == "reak")
                            {
                                result.Keyword = KeywordType.Break;
                                inCondition = true;
                            }
                            else if (letter == 'r' && value == "eturn")
                            {
                                result.Keyword = KeywordType.Return;
                                result.KeywordArgument = string.Empty;
                                inCondition = true;
                            }
                            else if (letter == 'a' && value == "bort")
                            {
                                result.Keyword = KeywordType.Abort;
                                inCondition = true;
                            }
                            else if (letter == 'v' && value == "ar")
                            {
                                result.Keyword = KeywordType.Var;
                                result.KeywordArgument = string.Empty;
                                inCondition = true;
                            }
                            else if (letter == 's' && value == "et")
                            {
                                result.Keyword = KeywordType.Set;
                                result.KeywordArgument = string.Empty;
                                inCondition = true;
                            }
                            else if (letter == 'e' && value == "cho")
                            {
                                result.Keyword = KeywordType.Echo;
                                result.KeywordArgument = string.Empty;
                                inCondition = true;
                            }
                            else if (result.Parameter(letter) == null)
                            {
                                AddParameter(result, char.ToUpperInvariant(letter), value, false, false);
                            }
                            else
                            {
                                throw new CodeParserException($"Duplicate {letter} parameter", result);
                            }
                        }
                        else if (letter == '\0' || result.Parameter(letter) == null)
                        {
                            if (!unprecedentedParameter)
                            {
                                letter = char.ToUpperInvariant(letter);
                            }
                            AddParameter(result, letter, value, wasQuoted, unprecedentedParameter || isNumericParameter || wasExpression);
                        }
                        else
                        {
                            throw new CodeParserException($"Duplicate {(letter == '\0' ? "unprecedented" : letter.ToString())} parameter", result);
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
                    else if (c == '(')
                    {
                        // Starting encapsulated comment
                        contentRead = inEncapsulatedComment = true;
                    }
                    else if (!char.IsWhiteSpace(c))
                    {
                        // Starting a new parameter
                        contentRead = inChunk = true;
                        if (c == '{')
                        {
                            value = "{";
                            inExpression = true;
                            inQuotes = false;
                        }
                        else if (c == '"')
                        {
                            inQuotes = true;
                        }
                        else
                        {
                            letter = c;
                        }
                    }
                }

                if (!inFinalComment && !inEncapsulatedComment && !inCondition && !inChunk)
                {
                    // Stop if another G/M/T code is coming up and this one is complete
                    int next = reader.Peek();
                    char nextChar = (next == -1) ? '\n' : char.ToUpperInvariant((char)next);
                    if (result.MajorNumber != null && result.MajorNumber != 53 && (nextChar == 'G' || nextChar == 'M' || nextChar == 'T') &&
                        (nextChar == 'M' || result.Type != CodeType.MCode || result.Parameters.Any(item => item.Letter == nextChar)))
                    {
                        // Note that M-codes may have G or T parameters but only one
                        break;
                    }
                }
            } while (c != '\n');
            seenNewLine |= (c == '\n');

            // Do not allow malformed codes
            if (inEncapsulatedComment)
            {
                throw new CodeParserException("Unterminated encapsulated comment", result);
            }
            if (inQuotes)
            {
                throw new CodeParserException("Unterminated string", result);
            }
            if (inExpression)
            {
                throw new CodeParserException("Unterminated expression", result);
            }
            if (result.KeywordArgument != null)
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
            if (result.Type == CodeType.MCode)
            {
                try
                {
                    switch (result.MajorNumber)
                    {
                        case 569:
                        case 915:
                            foreach (CodeParameter parameter in result.Parameters)
                            {
                                if (!parameter.IsExpression && char.ToUpperInvariant(parameter.Letter) == 'P')
                                {
                                    parameter.ConvertDriverIds(result);
                                }
                            }
                            break;

                        case 584:
                            foreach (CodeParameter parameter in result.Parameters)
                            {
                                char upper = char.ToUpperInvariant(parameter.Letter);
                                if (!parameter.IsExpression && (Machine.Axis.Letters.Contains(upper) || upper == 'E'))
                                {
                                    parameter.ConvertDriverIds(result);
                                }
                            }
                            break;
                    }
                }
                catch (CodeParserException e)
                {
                    throw new CodeParserException(e.Message, result);
                }
            }

            // End
            return contentRead;
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
            if (isQuoted || isSingleParameter)
            {
                code.Parameters.Add(new CodeParameter(letter, value, isQuoted, false));
            }
            else
            {
                code.Parameters.Add(new CodeParameter(letter, string.Empty, false, false));
                foreach (char c in value)
                {
                    code.Parameters.Add(new CodeParameter(c, string.Empty, false, false));
                }
            }
        }
    }
}
