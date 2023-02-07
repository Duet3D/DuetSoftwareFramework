using System;

namespace DuetAPI
{
    /// <summary>
    /// Wrapper around internal server-side exceptions that are reported as part of command responses
    /// </summary>
    /// <seealso cref="DuetAPI.Commands.ErrorResponse"/>
    public class InternalServerException : Exception
    {
        /// <summary>
        /// API command causing the exception
        /// </summary>
        public string Command { get; }

        /// <summary>
        /// Type of the thrown exception
        /// </summary>
        public string Type { get; }

        /// <summary>
        /// Constructor of this exception class
        /// </summary>
        /// <param name="command">Name of the command that failed</param>
        /// <param name="type">Type of the thrown .NET error</param>
        /// <param name="message">Message of the thrown .NET error</param>
        public InternalServerException(string command, string type, string message) : base(message)
        {
            Command = command;
            Type = type;
        }
    }
}