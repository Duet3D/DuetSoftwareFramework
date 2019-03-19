using System;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Connection;

namespace DuetControlServer.IPC.Processors
{
    /// <summary>
    /// Base class for connection interpreters
    /// </summary>
    /// <seealso cref="DuetAPI.Connection.ConnectionType"/>
    public abstract class Base
    {
        /// <summary>
        /// Connection with wrappers to ensure basic operation and convenience wrappers for JSON-style requests
        /// </summary>
        protected Connection Connection { get; }

        /// <summary>
        /// Base constructor for connection interpreters. Invoke this from any derived class
        /// </summary>
        /// <param name="conn">Connection instance</param>
        /// <param name="initMessage">Deserialized initialization message</param>
        public Base(Connection conn, ClientInitMessage initMessage)
        {
             Connection = conn;   
        }

        /// <summary>
        /// Worker method for a given connection.
        /// No <see cref="CancellationToken"/> is passed here, use <see cref="Program.CancelSource"/> instead.
        /// Once this task exits the connection is terminated.
        /// </summary>
        /// <returns>Task that represents the worker lifecycle</returns>
        /// <exception cref="NotImplementedException">Thrown if this method is not overridden</exception>
        public virtual Task Process()
        {
            throw new NotImplementedException("Processor not implemented");
        }
    }
}