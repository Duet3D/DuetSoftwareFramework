using System;

namespace DuetHttpClient.Exceptions
{
    /// <summary>
    /// Exception class that is thrown when an invalid password is used
    /// </summary>
    public class InvalidPasswordException : LoginException
    {
        /// <summary>
        /// Creates a new InvalidPasswordException
        /// </summary>
        public InvalidPasswordException() { }

        /// <summary>
        /// Creates a new InvalidPasswordException
        /// </summary>
        /// <param name="message">Exception message</param>
        public InvalidPasswordException(string message) : base(message) { }

        /// <summary>
        /// Creates a new InvalidPasswordException
        /// </summary>
        /// <param name="message">Exception message</param>
        /// <param name="inner">Inner exception</param>
        public InvalidPasswordException(string message, Exception inner) : base(message, inner) { }
    }
}
