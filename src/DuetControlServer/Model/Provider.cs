using System;
using System.Reflection;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Machine;
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
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Wrapper around the writer lock which notifies subscribers whenever an update has been processed
        /// </summary>
        private class WriterLockWrapper : IDisposable
        {
            /// <summary>
            /// Internal lock
            /// </summary>
            private readonly IDisposable _lock;

            /// <summary>
            /// Constructor of the lock wrapper
            /// </summary>
            /// <param name="item">Actual lock</param>
            internal WriterLockWrapper(IDisposable item) => _lock = item;

            /// <summary>
            /// Dipose method that is called when the lock is released
            /// </summary>
            public void Dispose()
            {
                // Notify subscribers and clear the messages if anyone is connected
                if (IPC.Processors.Subscription.AreClientsConnected())
                {
                    IPC.Processors.Subscription.ModelUpdated();
                    if (Get.Messages.Count > 0)
                    {
                        Get.Messages.Clear();
                    }
                }

                // Dispose the lock again 
                _lock.Dispose();
            }
        }

        /// <summary>
        /// Lock for read/write access
        /// </summary>
        private static readonly AsyncReaderWriterLock _lock = new AsyncReaderWriterLock();

        /// <summary>
        /// Initialize the object model provider with values that are not expected to change
        /// </summary>
        public static void Init()
        {
            Get.Electronics.Version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            Get.Electronics.Type = "duet3";
            Get.Electronics.Name = "Duet 3";
        }

        /// <summary>
        /// Access the machine model for read operations only
        /// </summary>
        /// <returns>Disposable lock object to be used with a using directive</returns>
        public static IDisposable AccessReadOnly() => _lock.ReaderLock();
        
        /// <summary>
        /// Access the machine model asynchronously for read operations only
        /// </summary>
        /// <returns>Disposable lock object to be used with a using directive</returns>
        public static AwaitableDisposable<IDisposable> AccessReadOnlyAsync() =>  _lock.ReaderLockAsync();

        /// <summary>
        /// Access the machine model for read/write operations
        /// </summary>
        /// <returns>Disposable lock object to be used with a using directive</returns>
        public static IDisposable AccessReadWrite() => new WriterLockWrapper(_lock.WriterLock());

        /// <summary>
        /// Access the machine model asynchronously for read/write operations
        /// </summary>
        /// <returns>Disposable lock object to be used with a using directive</returns>
        public static async Task<IDisposable> AccessReadWriteAsync()
        {
            IDisposable lockItem = await _lock.WriterLockAsync();
            return new WriterLockWrapper(lockItem);
        }

        /// <summary>
        /// Get the machine model. Make sure to call the acquire the corresponding lock first!
        /// </summary>
        /// <returns>Current Duet machine object model</returns>
        /// <seealso cref="AccessReadOnlyAsync()"/>
        /// <seealso cref="AccessReadWriteAsync()"/>
        public static MachineModel Get { get; } = new MachineModel();

        /// <summary>
        /// Output a generic message
        /// </summary>
        /// <param name="message">Message to output</param>
        /// <returns>Asynchronous task</returns>
        public static async Task Output(Message message)
        {
            if (!string.IsNullOrWhiteSpace(message?.Content))
            {
                // Print the message to the DCS log
                switch (message.Type)
                {
                    case MessageType.Error:
                        _logger.Error(message.Content);
                        break;
                    case MessageType.Warning:
                        _logger.Warn(message.Content);
                        break;
                    default:
                        _logger.Info(message.Content);
                        break;
                }

                // Attempt to forward messages directly to subscribers. If none are available,
                // append it to the object model so potential clients can fetch it for a limited period of time
                using (await AccessReadWriteAsync())
                {
                    Get.Messages.Add(message);
                }
            }
        }

        /// <summary>
        /// Output a generic message
        /// </summary>
        /// <param name="type">Type of the message</param>
        /// <param name="content">Content of the message</param>
        /// <returns>Asynchronous task</returns>
        public static Task Output(MessageType type, string content) => Output(new Message(type, content));

        /// <summary>
        /// Output the result of a G/M/T-code
        /// </summary>
        /// <param name="codeResult">Messages to output</param>
        /// <returns>Asynchronous task</returns>
        public static async Task Output(DuetAPI.Commands.CodeResult codeResult)
        {
            if (codeResult != null)
            {
                foreach (Message message in codeResult)
                {
                    await Output(message);
                }
            }
        }
    }
}