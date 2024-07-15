using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Connection;

namespace DuetControlServer.IPC.Processors
{
    /// <summary>
    /// Base class for connection interpreters
    /// </summary>
    /// <seealso cref="ConnectionMode"/>
    /// <remarks>
    /// Base constructor for connection interpreters. Invoke this from any derived class
    /// </remarks>
    /// <param name="conn">Connection instance</param>
    public class Base(Connection conn)
    {
        /// <summary>
        /// List of supported command types
        /// </summary>
        private static readonly List<Type> SupportedCommands = [];

        /// <summary>
        /// Add a list of supported commands
        /// </summary>
        /// <param name="supportedCommands">List of supported commands</param>
        protected static void AddSupportedCommands(IEnumerable<Type> supportedCommands) => SupportedCommands.AddRange(supportedCommands.Where(item => item is not null && !SupportedCommands.Contains(item)));

        /// <summary>
        /// Get the corresponding command type
        /// </summary>
        /// <param name="name">Name of the command</param>
        /// <returns>Command type or null if not found</returns>
        public static Type? GetCommandType(string name) => SupportedCommands.FirstOrDefault(item => item.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));

        /// <summary>
        /// Connection to the IPC client served by this processor
        /// </summary>
        protected Connection Connection { get; } = conn;

        /// <summary>
        /// Worker method for a given connection.
        /// No <see cref="CancellationToken"/> is passed here, use <see cref="Program.CancellationToken"/> instead.
        /// Once this task exits the connection is terminated.
        /// </summary>
        /// <returns>Task that represents the worker lifecycle</returns>
        /// <exception cref="NotImplementedException">Thrown if this method is not overridden</exception>
        public virtual Task Process() => throw new NotImplementedException("Processor not implemented");
    }
}