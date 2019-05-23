using DuetAPI.Commands;
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
        private readonly TaskCompletionSource<CodeResult> _taskSource = new TaskCompletionSource<CodeResult>();
        private bool _gotEmptyResponse = false;
        private bool _lastMessageIncomplete = false;        // true if the last message had the push flag set

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
        /// Whether the code is being executed internally or by the firmware
        /// </summary>
        public bool IsExecuting { get; set; }

        /// <summary>
        /// Indicates if RepRapFirmware requested a macro file for execution as part of this code
        /// </summary>
        public bool DoingNestedMacro { get; set; }

        /// <summary>
        /// Indicates if a complete G-code reply has been received implying this code can be finished
        /// </summary>
        public bool CanFinish { get => (_gotEmptyResponse || _result.Count != 0) && !_lastMessageIncomplete; }

        /// <summary>
        /// Task that is resolved when the code has finished
        /// </summary>
        public Task<CodeResult> Task { get => _taskSource.Task; }

        /// <summary>
        /// Process a code reply from the firmware
        /// </summary>
        /// <param name="messageType">Message type flags</param>
        /// <param name="reply">Raw code reply</param>
        public void HandleReply(Communication.MessageTypeFlags messageType, string reply)
        {
#if DEBUG
            Console.WriteLine($"[{Code} - {messageType}] {reply}");
#endif

            DuetAPI.Message message;
            if (reply == "")
            {
                _gotEmptyResponse = true;
                _lastMessageIncomplete = false;
            }
            else
            {
                if (_lastMessageIncomplete)
                {
                    message = _result[_result.Count - 1];
                    message.Content += reply;
                }
                else
                {
                    DuetAPI.MessageType type = messageType.HasFlag(Communication.MessageTypeFlags.ErrorMessageFlag) ? DuetAPI.MessageType.Error
                                : messageType.HasFlag(Communication.MessageTypeFlags.WarningMessageFlag) ? DuetAPI.MessageType.Warning
                                : DuetAPI.MessageType.Success;
                    message = new DuetAPI.Message(type, reply);
                    _result.Add(message);
                }
                _lastMessageIncomplete = messageType.HasFlag(Communication.MessageTypeFlags.PushFlag);
            }
        }

        /// <summary>
        /// Process a code reply
        /// </summary>
        /// <param name="result">Code reply</param>
        public void HandleReply(CodeResult result)
        {
#if DEBUG
            Console.WriteLine($"[{Code}] {result}");
#endif

            if (result != null)
            {
                _result.AddRange(result);
            }
            _gotEmptyResponse = true;
        }

        /// <summary>
        /// Report that soemthing went wrong while executing this code
        /// </summary>
        /// <param name="e">Exception to return</param>
        public void SetException(Exception e) => _taskSource.SetException(e);

        /// <summary>
        /// Called to resolve the task because it has finished
        /// </summary>
        public void SetFinished() => _taskSource.SetResult(_result);
    }
}
