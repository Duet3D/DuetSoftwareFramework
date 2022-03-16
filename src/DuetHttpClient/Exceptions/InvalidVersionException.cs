using System;

namespace DuetHttpClient.Exceptions
{
    /// <summary>
    /// Exception class that is thrown when the client cannot connect because the remote version is incompatible
    /// </summary>
    public class InvalidVersionException : LoginException
    {
        /// <summary>
        /// Creates a new InvalidVersionException
        /// </summary>
        public InvalidVersionException() { }

        /// <summary>
        /// Creates a new InvalidVersionException
        /// </summary>
        /// <param name="message">Exception message</param>
        public InvalidVersionException(string message) : base(message) { }

        /// <summary>
        /// Creates a new InvalidVersionException
        /// </summary>
        /// <param name="message">Exception message</param>
        /// <param name="inner">Inner exception</param>
        public InvalidVersionException(string message, Exception inner) : base(message, inner) { }
    }
}
