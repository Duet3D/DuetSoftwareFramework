using System;

namespace DuetAPIClient.Exceptions
{
    /// <summary>
    /// Wrapper around internal server-side exceptions that are reported as part of command responses
    /// </summary>
    public class InternalServerException : Exception
    {
        /// <summary>
        /// Creates a new CommandException instance
        /// </summary>
        /// <param name="command">Name of the command that failed</param>
        /// <param name="type">Type of the thrown .NET error</param>
        /// <param name="message">Message of the thrown .NET error</param>
        public InternalServerException(string command, string type, string message) : base($"Failed to execute {command}", MakeException(type, message)) { }

        /// <summary>
        /// Generates an exception from the given type and message
        /// </summary>
        /// <param name="type">Type of the thrown .NET error</param>
        /// <param name="message">Message of the thrown .NET error</param>
        /// <returns>Generated exception</returns>
        private static Exception MakeException(string type, string message)
        {
            try
            {
                return (Exception)Activator.CreateInstance(Type.GetType(type), message);
            }
            catch (MissingMethodException)
            {
                return new Exception($"{type}: {message}");
            }
        }
    }
}