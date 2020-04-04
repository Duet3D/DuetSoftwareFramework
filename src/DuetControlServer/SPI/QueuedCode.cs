using DuetAPI.Commands;
using DuetAPI.Machine;
using DuetControlServer.SPI.Communication.Shared;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.SPI
{
    /// <summary>
    /// Class that represents a queued code item.
    /// There is no need to serialize/deserialize data, so no properties in here 
    /// </summary>
    public class QueuedCode
    {
        private readonly CodeResult _result = new CodeResult();
        private readonly TaskCompletionSource<CodeResult> _tcs = new TaskCompletionSource<CodeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _lastMessageIncomplete;

        /// <summary>
        /// Constructor for a queued code
        /// </summary>
        /// <param name="code">Code to execute</param>
        public QueuedCode(Commands.Code code)
        {
            Code = code;
        }

        /// <summary>
        /// Code item to execute
        /// </summary>
        public Commands.Code Code { get; }

        /// <summary>
        /// Indicates if the code is ready to be sent to the firmware
        /// </summary>
        public bool IsReadyToSend { get; set; }

        /// <summary>
        /// Indicates if this code is currently suspended
        /// </summary>
        public bool IsSuspended { get; set; }

        /// <summary>
        /// Indicates if the code has been finished because of a G-code reply
        /// </summary>
        public bool IsFinished { get; private set; }

        /// <summary>
        /// Size of this code in binary representation
        /// </summary>
        public int BinarySize { get; set; }

        /// <summary>
        /// Indicates if RepRapFirmware requested a macro file for execution as part of this code
        /// </summary>
        public bool DoingNestedMacro { get; set; }

        /// <summary>
        /// Task that is resolved when the code has finished
        /// </summary>
        public Task<CodeResult> Task { get => _tcs.Task; }

        /// <summary>
        /// Process a code reply from the firmware
        /// </summary>
        /// <param name="messageType">Message type flags</param>
        /// <param name="reply">Raw code reply</param>
        public void HandleReply(MessageTypeFlags messageType, string reply)
        {
            if (string.IsNullOrEmpty(reply))
            {
                if (_result.Count == 0)
                {
                    _result.Add(MessageType.Success, string.Empty);
                }
            }
            else
            {
                if (_lastMessageIncomplete)
                {
                    Message message = _result[^1];
                    message.Content += reply;
                }
                else
                {
                    MessageType type = messageType.HasFlag(MessageTypeFlags.ErrorMessageFlag) ? MessageType.Error
                                : messageType.HasFlag(MessageTypeFlags.WarningMessageFlag) ? MessageType.Warning
                                : MessageType.Success;
                    _result.Add(type, reply);
                }
            }

            _lastMessageIncomplete = messageType.HasFlag(MessageTypeFlags.PushFlag);
            if (!_lastMessageIncomplete)
            {
                foreach (Message message in _result)
                {
                    message.Content = message.Content.TrimEnd();
                }

                if (!DoingNestedMacro)
                {
                    SetFinished();
                }
            }
        }

        /// <summary>
        /// Process a code reply
        /// </summary>
        /// <param name="result">Code reply</param>
        public void HandleReply(CodeResult result)
        {
            if (result != null && !result.IsEmpty)
            {
                _result.AddRange(result);
            }

            if (!DoingNestedMacro)
            {
                SetFinished();
            }
        }

        /// <summary>
        /// Report that this code has been cancelled
        /// </summary>
        public void SetCancelled()
        {
            IsFinished = true;
            _tcs.TrySetCanceled();
        }

        /// <summary>
        /// Report that something went wrong while executing this code
        /// </summary>
        /// <param name="e">Exception to return</param>
        public void SetException(Exception e)
        {
            IsFinished = true;
            _tcs.TrySetException(e);
        }

        /// <summary>
        /// Called to resolve the task because it has finished
        /// </summary>
        public void SetFinished()
        {
            IsFinished = true;
            _tcs.TrySetResult(_result);
        }

        /// <summary>
        /// Convert this instance to a string
        /// </summary>
        /// <returns>String representation</returns>
        public override string ToString() => Code.ToString();
    }
}
