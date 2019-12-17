using System;
using System.Threading.Tasks;

namespace DuetControlServer.IPC
{
    /// <summary>
    /// Static class to manage read/write locks of third-party plugins
    /// </summary>
    public static class LockManager
    {
        /// <summary>
        /// Source connection that acquired the current lock
        /// </summary>
        private static int _lockConnection = -1;

        /// <summary>
        /// Indicates if a third-party application has locked the object model for writing
        /// </summary>
        public static bool IsLocked { get => _lockConnection != -1; }

        /// <summary>
        /// Read/write lock held by a third-party plugins
        /// </summary>
        private static IDisposable _lock = null;

        /// <summary>
        /// Function to create a read/write lock to the object model
        /// </summary>
        /// <param name="sourceConnection">Source connection acquiring the lock</param>
        /// <returns>Asynchronous task</returns>
        public static async Task LockMachineModel(int sourceConnection)
        {
            _lock = await Model.Provider.AccessReadWriteAsync();
            _lockConnection = sourceConnection;
        }

        /// <summary>
        /// Unlock the machine model again
        /// </summary>
        /// <param name="sourceConnection">Source connection</param>
        public static void UnlockMachineModel(int sourceConnection)
        {
            if (_lockConnection == sourceConnection)
            {
                _lockConnection = -1;
                _lock?.Dispose();
                _lock = null;
            }
        }
    }
}
