using System;

namespace DuetAPIClient
{
    /// <summary>
    /// Wrapper around internal server-side exceptions that are reported as part of command responses
    /// </summary>
    /// <seealso cref="DuetAPI.Commands.ErrorResponse"/>
    public class InternalServerException : Exception
    {
        /// <summary>
        /// Creates a new CommandException instance
        /// </summary>
        /// <param name="command">Name of the command that failed</param>
        /// <param name="type">Type of the thrown .NET error</param>
        /// <param name="message">Message of the thrown .NET error</param>
        public InternalServerException(string command, string type, string message) : base(string.IsNullOrEmpty(message) ? $"{type} in {command}" : $"{type} in {command}: {message}") { }
    }
}