using DuetAPI.Commands;
using DuetAPI.Machine;
using DuetControlServer.SPI.Communication;
using DuetControlServer.SPI.Communication.Shared;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.SPI.Channel
{
    /// <summary>
    /// Class that represents a queued code that has been sent to RepRapFirmware
    /// </summary>
    public sealed class PendingCode
    {
        /// <summary>
        /// Internal code result to return when the code has finished
        /// </summary>
        private readonly CodeResult _result = new CodeResult();

        /// <summary>
        /// Task to complete when the code has finished
        /// </summary>
        private readonly TaskCompletionSource<CodeResult> _tcs = new TaskCompletionSource<CodeResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Code item to execute
        /// </summary>
        public Commands.Code Code { get; }

        /// <summary>
        /// Size of this code in binary representation
        /// </summary>
        public int BinarySize { get; }

        /// <summary>
        /// Constructor for a queued code
        /// </summary>
        /// <param name="code">Code to execute</param>
        public PendingCode(Commands.Code code)
        {
            Code = code;
            BinarySize = Consts.BufferedCodeHeaderSize + DataTransfer.GetCodeSize(code);
        }

        /// <summary>
        /// Task that is resolved when the code has finished
        /// </summary>
        public Task<CodeResult> Task { get => _tcs.Task; }

        /// <summary>
        /// Indicates if the code has been finished because of a G-code reply
        /// </summary>
        public bool IsFinished { get; private set; }

        /// <summary>
        /// Indicates if the last code reply was incomplete (i.e. sent with the Push flag)
        /// </summary>
        private bool _lastMessageIncomplete;

        /// <summary>
        /// Process a code reply from the firmware
        /// </summary>
        /// <param name="messageType">Message type flags</param>
        /// <param name="reply">Raw code reply</param>
        public void HandleReply(MessageTypeFlags messageType, string reply)
        {
            if (!string.IsNullOrEmpty(reply))
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
                SetFinished();
            }
        }

        /// <summary>
        /// Append a code reply
        /// </summary>
        /// <param name="result">Code reply</param>
        public void AppendReply(CodeResult result)
        {
            if (!result.IsEmpty)
            {
                _result.AddRange(result);
            }
        }

        /// <summary>
        /// Report that this code has been cancelled
        /// </summary>
        public void SetCancelled()
        {
            if (!IsFinished)
            {
                IsFinished = true;
                _tcs.SetCanceled();
            }
        }

        /// <summary>
        /// Report that something went wrong while executing this code
        /// </summary>
        /// <param name="e">Exception to return</param>
        public void SetException(Exception e)
        {
            if (!IsFinished)
            {
                IsFinished = true;
                _tcs.SetException(e);
            }
        }

        /// <summary>
        /// Called to resolve the task because it has finished
        /// </summary>
        public void SetFinished()
        {
            if (!IsFinished)
            {
                IsFinished = true;
                _tcs.SetResult(_result);
            }
        }

        /// <summary>
        /// Convert this instance to a string
        /// </summary>
        /// <returns>String representation</returns>
        public override string ToString() => Code.ToString();
    }
}
