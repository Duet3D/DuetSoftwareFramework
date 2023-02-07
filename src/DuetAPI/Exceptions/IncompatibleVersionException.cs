using System;

namespace DuetAPI
{
    /// <summary>
    /// Exception class that is thrown if the API version of the client is incompatible to the server
    /// </summary>
    public class IncompatibleVersionException : Exception
    {
        /// <summary>
        /// Creates a new exception instance
        /// </summary>
        public IncompatibleVersionException() { }

        /// <summary>
        /// Creates a new exception instance
        /// </summary>
        /// <param name="message">Error message</param>
        public IncompatibleVersionException(string message) : base(message) { }

        /// <summary>
        /// Creates a new exception instance
        /// </summary>
        /// <param name="message">Error message</param>
        /// <param name="innerException">Inner exception</param>
        public IncompatibleVersionException(string message, Exception innerException) : base(message, innerException) { }
    }
}