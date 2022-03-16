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
    public class Base
    {
        /// <summary>
        /// List of supported command types
        /// </summary>
        private static readonly List<Type> SupportedCommands = new();

        /// <summary>
        /// Add a list of supported commands
        /// </summary>
        /// <param name="supportedCommands">List of supported commands</param>
        protected static void AddSupportedCommands(IEnumerable<Type> supportedCommands) => SupportedCommands.AddRange(supportedCommands.Where(item => item != null && !SupportedCommands.Contains(item)));

        /// <summary>
        /// Get the corresponding command type
        /// </summary>
        /// <param name="name">Name of the command</param>
        /// <returns>Command type or null if not found</returns>
        public static Type GetCommandType(string name) => SupportedCommands.FirstOrDefault(item => item.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));

        /// <summary>
        /// Connection to the IPC client served by this processor
        /// </summary>
        protected Connection Connection { get; }

        /// <summary>
        /// Base constructor for connection interpreters. Invoke this from any derived class
        /// </summary>
        /// <param name="conn">Connection instance</param>
        public Base(Connection conn) => Connection = conn;

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