using System;

namespace DuetHttpClient.Exceptions
{
    /// <summary>
    /// Exception class that is thrown when the remote board does not have any more free sessions
    /// </summary>
    public class NoFreeSessionException : LoginException
    {
        /// <summary>
        /// Creates a new NoFreeSessionException
        /// </summary>
        public NoFreeSessionException() { }

        /// <summary>
        /// Creates a new NoFreeSessionException
        /// </summary>
        /// <param name="message">Exception message</param>
        public NoFreeSessionException(string message) : base(message) { }

        /// <summary>
        /// Creates a new NoFreeSessionException
        /// </summary>
        /// <param name="message">Exception message</param>
        /// <param name="inner">Inner exception</param>
        public NoFreeSessionException(string message, Exception inner) : base(message, inner) { }
    }
}
