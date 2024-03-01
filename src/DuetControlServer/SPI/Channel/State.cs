using DuetControlServer.Codes.Pipelines;
using DuetControlServer.Commands;
using DuetControlServer.Files;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DuetControlServer.SPI.Channel
{
    /// <summary>
    /// Representation of a stack level state
    /// </summary>
    public class State
    {
        /// <summary>
        /// Corresponding state on the pipeline
        /// </summary>
        private readonly PipelineStackItem _pipelineStackItem;

        /// <summary>
        /// Indicates if the motion system was active when this stack item was created
        /// </summary>
        public bool MotionSystemWasActive { get; }

        /// <summary>
        /// Constructor of this class
        /// </summary>
        /// <param name="stackItem">Corresponding state of the firmware stage on the code pipeline</param>
        public State(PipelineStackItem stackItem) => _pipelineStackItem = stackItem;

        /// <summary>
        /// Constructor of this class
        /// </summary>
        /// <param name="stackItem">Corresponding state of the firmware stage on the code pipeline</param>
        public State(PipelineStackItem stackItem, bool msActive)
        {
            _pipelineStackItem = stackItem;
            MotionSystemWasActive = msActive;
        }

        /// <summary>
        /// Indicates if this state is waiting for a confirmation
        /// </summary>
        public bool WaitingForAcknowledgement { get; set; }

        /// <summary>
        /// Queue of pending lock/unlock requests
        /// </summary>
        public Queue<LockRequest> LockRequests { get; } = new();

        /// <summary>
        /// Queue of suspended G/M/T-codes to resend when this state becomes active again
        /// </summary>
        public Queue<Code> SuspendedCodes { get; } = new();

        /// <summary>
        /// File being executed on this state
        /// </summary>
        /// <remarks>
        /// This is only assigned once after an instance has been created
        /// </remarks>
        public CodeFile? File { get => _pipelineStackItem.File; }

        /// <summary>
        /// Indicates if a macro was supposed to be opened but it failed
        /// </summary>
        public bool MacroError { get; set; }

        /// <summary>
        /// Indicates if the firmware has been notified about the macro completion
        /// </summary>
        public bool MacroCompleted { get; set; }

        /// <summary>
        /// Code that started this state
        /// </summary>
        public Code? StartCode { get; set; }

        /// <summary>
        /// Pending codes ready to be sent over to the firmware
        /// </summary>
        public Channel<Code> PendingCodes { get => _pipelineStackItem.PendingCodes; }

        /// <summary>
        /// Called to flag if the pipeline is busy or not
        /// </summary>
        public void SetBusy(bool isBusy)
        {
            lock (_pipelineStackItem)
            {
                _pipelineStackItem.Busy = isBusy;
            }
        }

        /// <summary>
        /// Queue of pending flush requests
        /// </summary>
        public Queue<TaskCompletionSource<bool>> FlushRequests { get; } = new();
    }
}
