using DuetAPI;
using System.Threading.Tasks;

namespace DuetControlServer.SPI
{
    /// <summary>
    /// Queued lock/unlock request
    /// </summary>
    public class QueuedLockRequest
    {
        private TaskCompletionSource<bool> _taskCompletionSource = new TaskCompletionSource<bool>();

        /// <summary>
        /// Indicates if this is a lock or unlock request
        /// </summary>
        public bool IsLockRequest { get; }

        /// <summary>
        /// Indicates if the lock request has been sent to the firmware
        /// </summary>
        public bool IsLockRequested { get; set; }

        /// <summary>
        /// Code channel that is supposed to acquire/release the lock
        /// </summary>
        public CodeChannel Channel { get; }

        /// <summary>
        /// Awaitable task returning true if the lock could be acquired.
        /// It returns false if the controller is reset or an emergency stop occurs
        /// </summary>
        public Task<bool> Task { get => _taskCompletionSource.Task; }

        /// <summary>
        /// Creates a new queued lock/unlock request instance
        /// </summary>
        /// <param name="isLockRequest">Whether the resource shall be locked</param>
        /// <param name="channel">Code channel requesting the lock</param>
        public QueuedLockRequest(bool isLockRequest, CodeChannel channel)
        {
            IsLockRequest = isLockRequest;
            Channel = channel;
        }

        /// <summary>
        /// Resolve the pending task with the given result
        /// </summary>
        /// <param name="lockAcquired">Whether the lock could be acquired</param>
        public void Resolve(bool lockAcquired)
        {
            _taskCompletionSource.SetResult(lockAcquired);
        }
    }
}
