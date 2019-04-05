using System;
using Nito.AsyncEx;

namespace DuetControlServer.Model
{
    /// <summary>
    /// Provider for the machine's object model to provides thread-safe read/write access.
    /// Make sure to access the machine model only when atomic operations are performed
    /// so that pending updates can be performed as quickly as possible.
    /// </summary>
    public static class Provider
    {
        private static readonly DuetAPI.Machine.Model _model = new DuetAPI.Machine.Model();
        private static readonly AsyncReaderWriterLock _lock = new AsyncReaderWriterLock();

        /// <summary>
        /// Initialize the object model provider
        /// </summary>
        public static void Init()
        {
            // Initialize electronics
            _model.Electronics.Type = "duet3";
            _model.Electronics.Name = "Duet 3";
            _model.Electronics.Revision = "0.5";

            // Initialize machine name
            _model.Network.Name = Environment.MachineName;
        }
        
        /// <summary>
        /// Access the machine model for read operations only
        /// </summary>
        /// <returns>Disposable lock object to be used with a using directive</returns>
        public static AwaitableDisposable<IDisposable> AccessReadOnly()
        {
            return _lock.ReaderLockAsync();
        }

        /// <summary>
        /// Access the machine model for read/write operations
        /// </summary>
        /// <returns>Disposable lock object to be used with a using directive</returns>
        public static AwaitableDisposable<IDisposable> AccessReadWrite()
        {
            return _lock.WriterLockAsync();
        }

        /// <summary>
        /// Get the machine model. Make sure to call the acquire the corresponding lock first!
        /// </summary>
        /// <returns>Current Duet machine object model</returns>
        /// <seealso cref="AccessReadOnly()"/>
        /// <seealso cref="AccessReadWrite()"/>
        public static DuetAPI.Machine.Model Get { get => _model; }
    }
}