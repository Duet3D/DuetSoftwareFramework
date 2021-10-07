using DuetAPI.Utility;
using System;
using System.Collections.Generic;
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
        /// <returns>Whether anything could be read</returns>
        /// <exception cref="CodeParserException">Thrown if the code contains errors like unterminated strings or unterminated comments</exception>
        /// <remarks>
        /// In general it is better to use <see cref="ParseAsync(StreamReader, Code, CodeParserBuffer)"/> because this implementation
        /// - does not update the line number unless it is specified using the 'N' character
        /// - does not set the corresponding flag for G53 after the first code on a line
        /// - sets the indentation level only for the first code in a line
        /// - does not support Fanuc or LaserWeb styles
        /// </remarks>
        public static bool Parse(TextReader reader, Code result)
        {
            char letter = '\0', c;
            string value = string.Empty;

            bool contentRead = false, unprecedentedParameter = false;
            bool inFinalComment = false, inEncapsulatedComment = false, inChunk = false, inQuotes = false, inExpression = false, inCondition = false;
            bool readingAtStart = true, isLineNumber = false, isNumericParameter = false, endingChunk = false;
            bool nextCharLowerCase = false, wasQuoted = false, wasExpression = false;
            int numCurlyBraces = 0, numRoundBraces = 0;

            char[] charArray = new char[1];
            Encoding encoding = (reader is StreamReader sr) ? sr.CurrentEncoding : Encoding.UTF8;
            result.Length = 0;
            do
            {
                // Read the next character
                int currentChar = reader.Read();
                c = (currentChar < 0) ? '\n' : (char)currentChar;
                charArray[0] = c;
                result.Length += encoding.GetByteCount(charArray);

                if (c == '\r')
                {
                    // Ignore CR
                    continue;
                }

                if (inFinalComment)
                {
                    // Reading a comment ending the current line
                    if (c != '\n')
                    {
                        // Add next character to the comment unless it is the "artificial" 0-character termination
                        result.Comment += c;
                    }
                    else if (result.Comment == null)
                    {
                        // Something started a comment, so the comment cannot be null any more
                        result.Comment = string.Empty;
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
                        // End of encapsulated comment
                        if (result.Comment == null)
                        {
                            // Something started a comment, so the comment cannot be null any more
                            result.Comment = string.Empty;
                        }
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

                    if (inCondition)
                    {
                        continue;
                    }
                }

                if (inChunk)
                {
                    if (inQuotes)
                    {
                        if (c == '\'')
                        {
                            if (nextCharLowerCase)
                            {
                                // Treat subsequent single-quotes as a single-quite char
                                value += '\'';
                            }
                            else
                            {
                                // Next letter should be lower-case
                                nextCharLowerCase = true;
                            }
                        }
                        else  if (c == '"')
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
                                inQuotes = nextCharLowerCase = false;
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
                            numCurlyBraces++;
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
                        if ((c == '{' && value.TrimEnd().EndsWith(':')) ||
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
                            if (long.TryParse(value, out long lineNumber))
                            {
                                result.LineNumber = lineNumber;
                            }
                            isLineNumber = false;
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
                            if (wasExpression)
                            {
                                if (result.Type == CodeType.TCode)
                                {
                                    AddParameter(result, 'T', value, false, true);
                                }
                                else
                                {
                                    throw new CodeParserException("Dynamic command numbers are only supported for T-codes");
                                }
                            }
                            else if (letter == 'g' && value == "lobal")
                            {
                                result.Type = CodeType.Keyword;
                                result.Keyword = KeywordType.Global;
                                result.KeywordArgument = string.Empty;
                                inCondition = true;
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
                                unprecedentedParameter = (upperLetter == 'M') && (majorNumber == 23 || majorNumber == 28 || majorNumber == 30 || majorNumber == 32 || majorNumber == 36 || majorNumber == 117);
                            }
                            else if (!string.IsNullOrWhiteSpace(value) || result.Type != CodeType.TCode)
                            {
                                throw new CodeParserException($"Failed to parse major {char.ToUpperInvariant((char)result.Type)}-code number ({value})", result);
                            }
                        }
                        else if (result.Type == CodeType.None && result.MajorNumber == null && !wasQuoted && !wasExpression)
                        {
                            // Check for conditional G-code
                            if (letter == 'i' && value == "f")
                            {
                                result.Type = CodeType.Keyword;
                                result.Keyword = KeywordType.If;
                                result.KeywordArgument = string.Empty;
                                inCondition = true;
                            }
                            else if (letter == 'e' && value == "lif")
                            {
                                result.Type = CodeType.Keyword;
                                result.Keyword = KeywordType.ElseIf;
                                result.KeywordArgument = string.Empty;
                                inCondition = true;
                            }
                            else if (letter == 'e' && value == "lse")
                            {
                                result.Type = CodeType.Keyword;
                                result.Keyword = KeywordType.Else;
                            }
                            else if (letter == 'w' && value == "hile")
                            {
                                result.Type = CodeType.Keyword;
                                result.Keyword = KeywordType.While;
                                result.KeywordArgument = string.Empty;
                                inCondition = true;
                            }
                            else if (letter == 'b' && value == "reak")
                            {
                                result.Type = CodeType.Keyword;
                                result.Keyword = KeywordType.Break;
                                inCondition = true;
                            }
                            else if (letter == 'c' && value == "ontinue")
                            {
                                result.Type = CodeType.Keyword;
                                result.Keyword = KeywordType.Continue;
                                inCondition = true;
                            }
                            else if (letter == 'a' && value == "bort")
                            {
                                result.Type = CodeType.Keyword;
                                result.Keyword = KeywordType.Abort;
                                inCondition = true;
                            }
                            else if (letter == 'v' && value == "ar")
                            {
                                result.Type = CodeType.Keyword;
                                result.Keyword = KeywordType.Var;
                                result.KeywordArgument = string.Empty;
                                inCondition = true;
                            }
                            else if (letter == 's' && value == "et")
                            {
                                result.Type = CodeType.Keyword;
                                result.Keyword = KeywordType.Set;
                                result.KeywordArgument = string.Empty;
                                inCondition = true;
                            }
                            else if (letter == 'e' && value == "cho")
                            {
                                result.Type = CodeType.Keyword;
                                result.Keyword = KeywordType.Echo;
                                result.KeywordArgument = string.Empty;
                                inCondition = true;
                            }
                            else if (result.Parameter(letter) == null)
                            {
                                AddParameter(result, char.ToUpperInvariant(letter), value, false, false);
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
                            else
                            {
                                letter = char.ToUpperInvariant(letter);
                            }

                            if (result.Parameter(letter) == null)
                            {
                                if (wasExpression && (!value.StartsWith('{') || !value.EndsWith('}')))
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
                    else if (!char.IsWhiteSpace(c))
                    {
                        // Starting a new parameter
                        contentRead = inChunk = true;
                        if (c == '{')
                        {
                            value = "{";
                            inExpression = true;
                            inQuotes = false;
                            numCurlyBraces++;
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
                    if ((nextChar == 'G' || nextChar == 'M' || nextChar == 'T') && result.Type != CodeType.None &&
                        (result.Type != CodeType.GCode || result.MajorNumber != 53) &&
                        (nextChar != 'T' || result.Type == CodeType.TCode || result.Parameters.Any(item => item.Letter == 'T')))
                    {
                        // Note that G- and M-codes may T parameters
                        break;
                    }
                }
            } while (c != '\n');


            // Check if this was the last code on the line
            if (c == '\n')
            {
                result.Flags |= CodeFlags.IsLastCode;
            }

            // Check if this is a whole-line comment
            if (result.Type == CodeType.None && result.Parameters.Count == 0 && result.Comment != null)
            {
                result.Type = CodeType.Comment;
            }

            // Do not allow malformed codes
            if (inEncapsulatedComment)
            {
                throw new CodeParserException("Unterminated encapsulated comment", result);
            }
            if (inQuotes)
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
            result.ConvertDriverIds();

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
                            char upper = char.ToUpperInvariant(parameter.Letter);
                            if (!parameter.IsExpression && (ObjectModel.Axis.Letters.Contains(upper) || upper == 'E'))
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
                List<DriverId> drivers = new();

                string[] parameters = parameter.StringValue.Split(':');
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
}
