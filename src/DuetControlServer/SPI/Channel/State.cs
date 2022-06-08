using DuetControlServer.Codes.PipelineStages;
using DuetControlServer.Commands;
using DuetControlServer.FileExecution;
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
        private readonly PipelineState _pipelineState;

        /// <summary>
        /// Constructor of this class
        /// </summary>
        /// <param name="pipelineState">Corresponding state of the firmware stage on the code pipeline</param>
        public State(PipelineState pipelineState) => _pipelineState = pipelineState;

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
        /// Macro being executed on this state
        /// </summary>
        /// <remarks>
        /// This is only assigned once after an instance has been created
        /// </remarks>
        public Macro Macro { get => _pipelineState.Macro; }

        /// <summary>
        /// Indicates if the firmware has been notified about the macro completion
        /// </summary>
        public bool MacroCompleted { get; set; }

        /// <summary>
        /// Code that started this state
        /// </summary>
        public Code StartCode { get; set; }

        /// <summary>
        /// Pending codes ready to be sent over to the firmware
        /// </summary>
        public Channel<Code> PendingCodes { get => _pipelineState.PendingCodes; }

        /// <summary>
        /// Queue of pending flush requests
        /// </summary>
        public Queue<TaskCompletionSource<bool>> FlushRequests { get; } = new();
    }
}
