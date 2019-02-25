using System;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Exception class that is thrown when a G/M/T-code could not be parsed
    /// </summary>
    public class CodeParserException : Exception
    {
        public CodeParserException() { }
        public CodeParserException(string message) : base(message) { }
        public CodeParserException(string message, Exception inner) : base(message, inner) { }
    }
}