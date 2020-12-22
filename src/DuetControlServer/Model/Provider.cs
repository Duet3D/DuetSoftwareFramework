using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
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
        /// Wrapper around the lock which notifies subscribers whenever an update has been processed.
        /// It is also able to detect the origin of model-related deadlocks
        /// </summary>
        private sealed class LockWrapper : IDisposable
        {
            /// <summary>
            /// Internal lock
            /// </summary>
            private readonly IDisposable _lock;

            /// <summary>
            /// Indicates if this lock is meant for write access
            /// </summary>
            private readonly bool _isWriteLock;

            /// <summary>
            /// CTS to trigger when the lock is being released
            /// </summary>
            private readonly CancellationTokenSource _releaseCts;

            /// <summary>
            /// Constructor of the lock wrapper
            /// </summary>
            /// <param name="lockItem">Actual lock</param>
            /// <param name="isWriteLock">Whether the lock is a read/write lock</param>
            /// <param name="cancellationToken">Internal cancellation token</param>
            internal LockWrapper(IDisposable lockItem, bool isWriteLock)
            {
                _lock = lockItem;
                _isWriteLock = isWriteLock;

                if (Settings.MaxMachineModelLockTime > 0)
                {
                    _releaseCts = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);

                    StackTrace stackTrace = new StackTrace(true);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(Settings.MaxMachineModelLockTime, _releaseCts.Token);
                            _logger.Fatal("{0} deadlock detected, stack trace of the deadlock:\n{1}", isWriteLock ? "Writer" : "Reader", stackTrace);
                            Program.CancelSource.Cancel();
                        }
                        finally
                        {
                            _releaseCts.Dispose();
                        }
                    });
                }
            }

            /// <summary>
            /// Dipose method that is called when the lock is released
            /// </summary>
            public void Dispose()
            {
                try
                {
                    if (_isWriteLock)
                    {
                        // It is safe to assume that the object model has been updated
                        using (_updateLock.Lock(Program.CancellationToken))
                        {
                            _updateEvent.NotifyAll();
                        }

                        // Clear the messages again if anyone is connected
                        if (IPC.Processors.ModelSubscription.AreClientsConnected && Get.Messages.Count > 0)
                        {
                            Get.Messages.Clear();
                        }
                    }
                }
                finally
                {
                    // Dispose the lock again
                    _lock.Dispose();

                    // Stop the deadlock detection task if applicable
                    if (!Program.CancelSource.IsCancellationRequested)
                    {
                        _releaseCts?.Cancel();
                    }
                }
            }
        }

        /// <summary>
        /// Lock for read/write access
        /// </summary>
        private static readonly AsyncReaderWriterLock _readWriteLock = new AsyncReaderWriterLock();

        /// <summary>
        /// Base lock for update conditions
        /// </summary>
        private static readonly AsyncLock _updateLock = new AsyncLock();

        /// <summary>
        /// Condition variable to trigger when the machine model has been updated
        /// </summary>
        private static readonly AsyncConditionVariable _updateEvent = new AsyncConditionVariable(_updateLock);

        /// <summary>
        /// Get the machine model. Make sure to call the acquire the corresponding lock first!
        /// </summary>
        /// <returns>Current Duet machine object model</returns>
        /// <seealso cref="AccessReadOnlyAsync()"/>
        /// <seealso cref="AccessReadWriteAsync()"/>
        /// <seealso cref="WaitForUpdate(CancellationToken)"/>
        /// <seealso cref="Updater.WaitForFullUpdate(CancellationToken)"/>
        public static ObjectModel Get { get; } = new ObjectModel();

        /// <summary>
        /// Whether the current machine status is overridden because an update is in progress
        /// </summary>
        public static bool IsUpdating
        {
            get => _isUpdating;
            set
            {
                if (value)
                {
                    Get.State.Status = MachineStatus.Updating;
                }
                _isUpdating = value;
            }
        }
        private static bool _isUpdating;

        /// <summary>
        /// Initialize the object model provider with values that are not expected to change
        /// </summary>
        public static void Init()
        {
            Get.Move.Compensation.PropertyChanged += Compensation_PropertyChanged;
            Get.State.DsfVersion = Program.Version;
            Get.Network.Hostname = Environment.MachineName;
            Get.Network.Name = Environment.MachineName;
        }

        /// <summary>
        /// Change handler for move.compensation
        /// </summary>
        /// <param name="sender">Sender object</param>
        /// <param name="e">Event arguments</param>
        private static void Compensation_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MoveCompensation.Type) && Get.Move.Compensation.Type == MoveCompensationType.None)
            {
                Get.Move.Compensation.File = null;
            }
        }

        /// <summary>
        /// Access the machine model for read operations only
        /// </summary>
        /// <returns>Disposable lock object to be used with a using directive</returns>
        public static IDisposable AccessReadOnly(CancellationToken cancellationToken)
        {
            return new LockWrapper(_readWriteLock.ReaderLock(cancellationToken), false);
        }

        /// <summary>
        /// Access the machine model for read operations only
        /// </summary>
        /// <returns>Disposable lock object to be used with a using directive</returns>
        public static IDisposable AccessReadOnly() => AccessReadOnly(Program.CancellationToken);

        /// <summary>
        /// Access the machine model for read/write operations
        /// </summary>
        /// <returns>Disposable lock object to be used with a using directive</returns>
        public static IDisposable AccessReadWrite(CancellationToken cancellationToken)
        {
            return new LockWrapper(_readWriteLock.WriterLock(cancellationToken), true);
        }

        /// <summary>
        /// Access the machine model for read/write operations
        /// </summary>
        /// <returns>Disposable lock object to be used with a using directive</returns>
        public static IDisposable AccessReadWrite() => AccessReadWrite(Program.CancellationToken);

        /// <summary>
        /// Access the machine model asynchronously for read operations only
        /// </summary>
        /// <returns>Disposable lock object to be used with a using directive</returns>
        public static async Task<IDisposable> AccessReadOnlyAsync(CancellationToken cancellationToken)
        {
            return new LockWrapper(await _readWriteLock.ReaderLockAsync(cancellationToken), false);
        }

        /// <summary>
        /// Access the machine model asynchronously for read operations only
        /// </summary>
        /// <returns>Disposable lock object to be used with a using directive</returns>
        public static Task<IDisposable> AccessReadOnlyAsync() => AccessReadOnlyAsync(Program.CancellationToken);

        /// <summary>
        /// Access the machine model asynchronously for read/write operations
        /// </summary>
        /// <returns>Disposable lock object to be used with a using directive</returns>
        public static async Task<IDisposable> AccessReadWriteAsync(CancellationToken cancellationToken)
        {
            return new LockWrapper(await _readWriteLock.WriterLockAsync(cancellationToken), true);
        }

        /// <summary>
        /// Access the machine model asynchronously for read/write operations
        /// </summary>
        /// <returns>Disposable lock object to be used with a using directive</returns>
        public static Task<IDisposable> AccessReadWriteAsync() => AccessReadWriteAsync(Program.CancellationToken);

        /// <summary>
        /// Wait for an update to occur
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public static async Task WaitForUpdate(CancellationToken cancellationToken)
        {
            using (await _updateLock.LockAsync(cancellationToken))
            {
                await _updateEvent.WaitAsync(cancellationToken);
                Program.CancelSource.Token.ThrowIfCancellationRequested();
            }
        }

        /// <summary>
        /// Wait for an update to occur
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public static Task WaitForUpdate() => WaitForUpdate(Program.CancellationToken);

        /// <summary>
        /// Output a generic message
        /// </summary>
        /// <param name="level">Log level</param>
        /// <param name="message">Message to output</param>
        /// <returns>Whether the message has been written</returns>
        public static async Task<bool> Output(LogLevel level, Message message)
        {
            if (!string.IsNullOrWhiteSpace(message?.Content))
            {
                using (await AccessReadWriteAsync())
                {
                    // Can we output this message?
                    if (Get.State.LogLevel == LogLevel.Off || (byte)Get.State.LogLevel + (byte)level < 3)
                    {
                        return false;
                    }

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

                    // Send it to the object model
                    Get.Messages.Add(message);
                }

                return true;
            }
            return false;
        }
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

                // Send it to the object model
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