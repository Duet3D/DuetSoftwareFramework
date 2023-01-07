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
        /// Connection that acquired the current lock
        /// </summary>
        private static Connection? _lockConnection;

        /// <summary>
        /// Indicates if a third-party application has locked the object model for writing
        /// </summary>
        public static bool IsLocked => _lockConnection is not null;

        /// <summary>
        /// Read/write lock held by a third-party plugins
        /// </summary>
        private static IDisposable? _lock;

        /// <summary>
        /// Function to create a read/write lock to the object model
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task LockMachineModel(Connection connection)
        {
            _lock = await Model.Provider.AccessReadWriteAsync();
            _lockConnection = connection;
        }

        /// <summary>
        /// Unlock the machine model again
        /// </summary>
        public static async Task UnlockMachineModel(Connection connection)
        {
            if (_lockConnection == connection)
            {
                _lockConnection = null;
                _lock?.Dispose();
                _lock = null;

                if (Settings.NoSpi)
                {
                    // Make sure functions waiting for full model updates don't stall
                    await Model.Updater.MachineModelFullyUpdated();
                }
            }
        }
    }
}
