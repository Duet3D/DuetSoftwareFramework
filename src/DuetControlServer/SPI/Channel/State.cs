using DuetControlServer.FileExecution;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DuetControlServer.SPI.Channel
{
    /// <summary>
    /// Representation of a stack level state
    /// </summary>
    public class State
    {
        /// <summary>
        /// Indicates if this state is waiting for a confirmation
        /// </summary>
        public bool WaitingForAcknowledgement { get; set; }

        /// <summary>
        /// Queue of pending lock/unlock requests
        /// </summary>
        public Queue<LockRequest> LockRequests { get; } = new Queue<LockRequest>();

        /// <summary>
        /// Queue of suspended G/M/T-codes to resend when this state becomes active again
        /// </summary>
        public Queue<PendingCode> SuspendedCodes { get; } = new Queue<PendingCode>();

        /// <summary>
        /// Macro being executed on this state
        /// </summary>
        /// <remarks>
        /// This is only assigned once after an instance has been created
        /// </remarks>
        public Macro Macro { get; set; }

        /// <summary>
        /// Indicates if the firmware has been notified about the macro completion
        /// </summary>
        public bool MacroCompleted { get; set; }

        /// <summary>
        /// Code that started the macro file of this state
        /// </summary>
        public PendingCode MacroStartCode { get; set; }

        /// <summary>
        /// Queue of pending G/M/T-codes that have not been buffered yet
        /// </summary>
        public Queue<PendingCode> PendingCodes { get; } = new Queue<PendingCode>();

        /// <summary>
        /// Queue of pending flush requests
        /// </summary>
        public Queue<TaskCompletionSource<bool>> FlushRequests { get; } = new Queue<TaskCompletionSource<bool>>();
    }
}
