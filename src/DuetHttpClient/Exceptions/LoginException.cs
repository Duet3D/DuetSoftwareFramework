using System;

namespace DuetHttpClient.Exceptions
{
    /// <summary>
    /// Base exception class that is thrown when the Duet controller refused the connection
    /// </summary>
    public class LoginException : Exception
    {
        /// <summary>
        /// Creates a new CodeParserException
        /// </summary>
        public LoginException() { }

        /// <summary>
        /// Creates a new CodeParserException
        /// </summary>
        /// <param name="message">Exception message</param>
        public LoginException(string message) : base(message) { }

        /// <summary>
        /// Creates a new CodeParserException
        /// </summary>
        /// <param name="message">Exception message</param>
        /// <param name="inner">Inner exception</param>
        public LoginException(string message, Exception inner) : base(message, inner) { }
    }
}
