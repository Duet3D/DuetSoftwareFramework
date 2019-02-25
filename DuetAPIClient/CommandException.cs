using System;

namespace DuetAPI
{
    /// <summary>
    /// Wrapper around server-side exceptions
    /// </summary>
    public class CommandException : Exception
    {
        public CommandException(string command, string type, string message) : base($"Failed to execute {command}", MakeException(type, message))
        {
        }

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