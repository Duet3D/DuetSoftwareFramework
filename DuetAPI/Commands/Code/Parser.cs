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

            bool inQuotes = false, inEncapsulatedComment = false, inFinalComment = false;
            bool isMajorCode = false, expectMinorCode = false, isMinorCode = false;
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
                    // Get the code type. T-codes can follow M-codes so allow them as potential parameters
                    // Also allow major numbers after G53 to support enforcement of absolute positions
                    if ((MajorNumber == -1 || MajorNumber == 53) && (c == 'G' || c == 'M' || c == 'T'))
                    {
                        if (Type == CodeType.GCode && MajorNumber == 53)
                        {
                            MajorNumber = 0;
                            EnforceAbsoluteCoordinates = true;
                        }
                        Type = (CodeType)c;
                        isMajorCode = true;
                    }
                    // Null characters, white spaces or dots following the major code indicate an end of the current chunk
                    else if (c == '\0' || char.IsWhiteSpace(c) || (c == '.' && isMajorCode))
                    {
                        if (isMajorCode)
                        {
                            if (int.TryParse(paramValue.Trim(), out int majorCode))
                            {
                                MajorNumber = majorCode;
                                paramValue = "";

                                isMajorCode = false;
                                expectMinorCode = (c != '.');
                                isMinorCode = (c == '.');
                            }
                            else
                            {
                                throw new CodeParserException($"Failed to parse major {Type} number ({paramValue.Trim()})");
                            }
                        }
                        else if (isMinorCode)
                        {
                            if (int.TryParse(paramValue.Trim(), out int minorCode))
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
                        else if (paramLetter != '\0')
                        {
                            Parameters.Add(new CodeParameter(paramLetter, paramValue, inQuotes));
                            paramLetter = '\0';
                            paramValue = "";
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
                        if (Comment == null)
                        {
                            Comment = "";
                        }
                        inFinalComment = (c == ';');
                        inEncapsulatedComment = (c == '(');
                    }
                    // Start new parameter on demand
                    else if (paramLetter == '\0' && !isMajorCode && !isMinorCode)
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
            if (Parameters.Count > 255)
            {
                throw new CodeParserException("Too many parameters (> 255)");
            }
        }
    }
}
