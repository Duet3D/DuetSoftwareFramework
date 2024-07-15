using System;

namespace DuetAPI
{
    /// <summary>
    /// Wrapper around internal server-side exceptions that are reported as part of command responses
    /// </summary>
    /// <seealso cref="DuetAPI.Commands.ErrorResponse"/>
    /// <param name="command">Name of the command that failed</param>
    /// <param name="type">Type of the thrown .NET error</param>
    /// <param name="message">Message of the thrown .NET error</param>
    public class InternalServerException(string command, string type, string message) : Exception(message)
    {
        /// <summary>
        /// API command causing the exception
        /// </summary>
        public string Command { get; } = command;

        /// <summary>
        /// Type of the thrown exception
        /// </summary>
        public string Type { get; } = type;
    }
}