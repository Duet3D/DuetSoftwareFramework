using System;

namespace DuetAPI.Commands
{
    public class CodeParserException : Exception
    {
        public CodeParserException() { }
        public CodeParserException(string message) : base(message) { }
        public CodeParserException(string message, Exception inner) : base(message, inner) { }
    }
}