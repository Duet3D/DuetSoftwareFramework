namespace DuetAPI.Commands
{
    public partial class Code
    {
        /// <summary>
        /// Create a parsed code representation from a text-based string
        /// This constructor parses the whole code and fills the class members where applicable
        /// </summary>
        /// <param name="codeString">The text-based G/M/T-code</param>
        /// <exception cref="CodeParserException">Thrown if the code contains errors like unterminated strings or unterminated comments</exception>
        public Code(string codeString)
        {
            char paramLetter = '\0';
            string paramValue = "";

            bool inQuotes = false, wasQuoted = false, inEncapsulatedComment = false, inFinalComment = false;
            bool isCondition = false, isLineNumber = false, isMajorCode = false, expectMinorCode = false, isMinorCode = false;
            for (int i = 0; i <= codeString.Length; i++)
            {
                char c = (i == codeString.Length) ? '\0' : codeString[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i < codeString.Length - 1 && codeString[i + 1] == '"')
                        {
                            // Treat subsequent dobule quotes as a single quote char
                            paramValue += '"';
                            i++;
                        }
                        else
                        {
                            // No longer in an escaped parameter
                            inQuotes = false;
                            wasQuoted = true;
                        }
                    }
                    else
                    {
                        // Add next character to the parameter value
                        paramValue += c;
                    }
                }
                else if (inEncapsulatedComment)
                {
                    if (c == ')')
                    {
                        // Even though RepRapFirmware treats comments in braces differently,
                        // the correct approach should be to switch back to reading mode when the comment tag is closed
                        inEncapsulatedComment = false;
                    }
                    else
                    {
                        // Add next character to the comment
                        Comment += c;
                    }
                }
                else if (inFinalComment)
                {
                    if (c != '\0')
                    {
                        // Add next character to the comment unless it is the "artificial" 0-character termination
                        Comment += c;
                    }
                }
                else
                {
                    // Line numbers prefix the major code
                    if (!MajorNumber.HasValue && !LineNumber.HasValue && c == 'N')
                    {
                        isLineNumber = true;
                    }
                    // Read the condition body. Except for comments this is expected to fill the entire line
                    else if (isCondition)
                    {
                        switch (c)
                        {
                            case '\0':
                                // Ignore terminating zero
                                break;
                            case ';':
                                inFinalComment = true;
                                break;
                            case '(':
                                inEncapsulatedComment = true;
                                break;
                            default:
                                KeywordArgument += c;
                                break;
                        }
                    }
                    // Get the code type. T-codes can follow M-codes so allow them as potential parameters
                    // Also allow major numbers after G53 to support enforcement of absolute positions
                    else if ((!MajorNumber.HasValue || MajorNumber == 53) && (c == 'G' || c == 'M' || c == 'T'))
                    {
                        if (Type == CodeType.GCode && MajorNumber == 53)
                        {
                            MajorNumber = null;
                            Flags |= CodeFlags.EnforceAbsolutePosition;
                        }
                        Type = (CodeType)c;
                        isMajorCode = true;
                        isCondition = false;
                    }
                    // Null characters, white spaces or dots following the major code indicate an end of the current chunk
                    else if (c == '\0' || char.IsWhiteSpace(c) || (c == '.' && isMajorCode))
                    {
                        if (isLineNumber)
                        {
                            if (long.TryParse(paramValue.Trim(), out long lineNumber))
                            {
                                LineNumber = lineNumber;
                                paramValue = "";

                                isLineNumber = false;
                            }
                        }
                        else if (isMajorCode)
                        {
                            if (int.TryParse(paramValue.Trim(), out int majorCode))
                            {
                                MajorNumber = majorCode;
                                paramValue = "";

                                isMajorCode = false;
                                expectMinorCode = (c != '.');
                                isMinorCode = (c == '.');
                            }
                        }
                        else if (isMinorCode)
                        {
                            if (sbyte.TryParse(paramValue.Trim(), out sbyte minorCode))
                            {
                                MinorNumber = minorCode;
                                paramValue = "";

                                isMinorCode = false;
                            }
                            else
                            {
                                throw new CodeParserException($"Failed to parse minor {Type} number ({paramValue.Trim()})");
                            }
                        }
                        else if (paramLetter != '\0' || paramValue != "")
                        {
                            if (!MajorNumber.HasValue && Keyword == KeywordType.None && !wasQuoted)
                            {
                                // Check if this is a conditional G-code
                                if (paramLetter == 'i' && paramValue == "f")
                                {
                                    Keyword = KeywordType.If;
                                    isCondition = true;
                                }
                                else if (paramLetter == 'e' && paramValue == "lif")
                                {
                                    Keyword = KeywordType.ElseIf;
                                    isCondition = true;
                                }
                                else if (paramLetter == 'e' && paramValue == "lse")
                                {
                                    Keyword = KeywordType.Else;
                                }
                                else if (paramLetter == 'w' && paramValue == "hile")
                                {
                                    Keyword = KeywordType.While;
                                    isCondition = true;
                                }
                                else if (paramLetter == 'b' && paramValue == "reak")
                                {
                                    Keyword = KeywordType.Break;
                                    isCondition = true;
                                }
                                else if (paramLetter == 'r' && paramValue == "eturn")
                                {
                                    Keyword = KeywordType.Return;
                                    isCondition = true;
                                }
                                else if (paramLetter == 'a' && paramValue == "bort")
                                {
                                    Keyword = KeywordType.Abort;
                                    isCondition = true;
                                }
                                else if (paramLetter == 'v' && paramValue == "ar")
                                {
                                    Keyword = KeywordType.Var;
                                    isCondition = true;
                                }
                                else if (paramLetter == 's' && paramValue == "et")
                                {
                                    Keyword = KeywordType.Set;
                                    isCondition = true;
                                }
                                else
                                {
                                    Parameters.Add(new CodeParameter(paramLetter, paramValue, false));
                                }

                                if (isCondition)
                                {
                                    KeywordArgument = "";
                                }
                            }
                            else
                            {
                                Parameters.Add(new CodeParameter(paramLetter, paramValue, wasQuoted));
                                wasQuoted = false;
                            }

                            paramLetter = '\0';
                            paramValue = "";
                        }
                        else if (!MajorNumber.HasValue && char.IsWhiteSpace(c))
                        {
                            Indent++;
                        }
                    }
                    // If the optional minor code number is expected to follow, read it once a dot is seen
                    else if (expectMinorCode && c == '.')
                    {
                        expectMinorCode = false;
                        isMinorCode = true;
                    }
                    // Deal with escaped string parameters
                    else if (c == '"')
                    {
                        inQuotes = true;
                    }
                    // Deal with comments
                    else if (c == ';' || c == '(')
                    {
                        if (paramLetter != '\0' || paramValue != "")
                        {
                            Parameters.Add(new CodeParameter(paramLetter, paramValue, wasQuoted));
                            wasQuoted = false;

                            paramLetter = '\0';
                            paramValue = "";
                        }

                        if (Comment == null)
                        {
                            Comment = "";
                        }
                        inFinalComment = (c == ';');
                        inEncapsulatedComment = (c == '(');
                    }
                    // Start new parameter on demand
                    else if (paramLetter == '\0' && !isLineNumber && !isMajorCode && !isMinorCode)
                    {
                        expectMinorCode = false;
                        paramLetter = c;
                    }
                    // Add the next letter to the current chunk
                    else
                    {
                        paramValue += c;
                    }
                }
            }

            // Do not allow malformed codes
            if (inQuotes)
            {
                throw new CodeParserException("Unterminated string parameter");
            }
            if (inEncapsulatedComment)
            {
                throw new CodeParserException("Unterminated encapsulated comment");
            }
            if (KeywordArgument != null)
            {
                KeywordArgument = KeywordArgument.Trim();
                if (KeywordArgument.Length > 255)
                {
                    throw new CodeParserException("Keyword argument too long (> 255)");
                }
            }
            if (Parameters.Count > 255)
            {
                throw new CodeParserException("Too many parameters (> 255)");
            }
        }
    }
}
