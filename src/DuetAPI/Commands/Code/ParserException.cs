using System;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Exception class that is thrown when a G/M/T-code could not be parsed
    /// </summary>
    public class CodeParserException : Exception
    {
        /// <summary>
        /// Creates a new CodeParserException
        /// </summary>
        public CodeParserException() { }

        /// <summary>
        /// Creates a new CodeParserException
        /// </summary>
        /// <param name="message">Exception message</param>
        public CodeParserException(string message) : base(message) { }

        /// <summary>
        /// Creates a new CodeParserException with details where the parser failed to read data
        /// </summary>
        /// <param name="message">Exception message</param>
        /// <param name="code">Code being parsed</param>
        public CodeParserException(string message, Code code)
            : base(message + ((code.LineNumber != null) ? $" in line {code.LineNumber}" : string.Empty)) { }

        /// <summary>
        /// Creates a new CodeParserException
        /// </summary>
        /// <param name="message">Exception message</param>
        /// <param name="inner">Inner exception</param>
        public CodeParserException(string message, Exception inner) : base(message, inner) { }
    }
}