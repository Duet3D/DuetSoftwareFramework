using System;
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
        /// No <see cref="CancellationToken"/> is passed here, use <see cref="Program.CancelSource"/> instead.
        /// Once this task exits the connection is terminated.
        /// </summary>
        /// <returns>Task that represents the worker lifecycle</returns>
        /// <exception cref="NotImplementedException">Thrown if this method is not overridden</exception>
        public virtual Task Process() => throw new NotImplementedException("Processor not implemented");
    }
}