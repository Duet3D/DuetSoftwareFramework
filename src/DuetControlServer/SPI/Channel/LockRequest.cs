using System.Threading.Tasks;

namespace DuetControlServer.SPI.Channel
{
    /// <summary>
    /// Queued lock/unlock request
    /// </summary>
    /// <remarks>
    /// Creates a new queued lock/unlock request instance
    /// </remarks>
    /// <param name="isLockRequest">Whether the resource shall be locked</param>
    public class LockRequest(bool isLockRequest)
    {
        /// <summary>
        /// Task completion source that completes when the lock request has been resolved
        /// </summary>
        private readonly TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Indicates if this is a lock or unlock request
        /// </summary>
        public bool IsLockRequest { get; } = isLockRequest;

        /// <summary>
        /// Indicates if the lock request has been sent to the firmware
        /// </summary>
        public bool IsLockRequested { get; set; }

        /// <summary>
        /// Awaitable task returning true if the lock could be acquired.
        /// It returns false if the controller is reset or an emergency stop occurs
        /// </summary>
        public Task<bool> Task => _tcs.Task;

        /// <summary>
        /// Resolve the pending task with the given result
        /// </summary>
        /// <param name="lockAcquired">Whether the lock could be acquired</param>
        public void Resolve(bool lockAcquired) => _tcs.SetResult(lockAcquired);
    }
}
