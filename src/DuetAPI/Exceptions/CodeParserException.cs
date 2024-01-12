using System;

namespace DuetAPI
{
    /// <summary>
    /// Exception that is thrown when a G/M/T-code could not be parsed
    /// </summary>
    public class CodeParserException : Exception
    {
        /// <summary>
        /// Code causing the error
        /// </summary>
        public Commands.Code? Code { get; }

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
        public CodeParserException(string message, Commands.Code code) : base(message) => Code = code;

        /// <summary>
        /// Creates a new CodeParserException
        /// </summary>
        /// <param name="message">Exception message</param>
        /// <param name="inner">Inner exception</param>
        public CodeParserException(string message, Exception inner) : base(message, inner)
        {
            if (inner is CodeParserException cpe)
            {
                Code = cpe.Code;
            }
        }
    }
}