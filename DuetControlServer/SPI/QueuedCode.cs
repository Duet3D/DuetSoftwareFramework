using DuetAPI.Commands;
using DuetControlServer.SPI.Communication.FirmwareRequests;
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
        private CodeResult _result = new CodeResult();
        private bool _gotEmptyResponse = false;
        private bool _lastMessageIncomplete = false;        // true if the last message had the push flag set
        private TaskCompletionSource<CodeResult> _taskSource;

        /// <summary>
        /// Constructor for a queued code
        /// </summary>
        /// <param name="code">Code to execute</param>
        /// <param name="fromFirmwareRequest">Whether this code comes from a firmware request</param>
        public QueuedCode(Commands.Code code, bool fromFirmwareRequest)
        {
            Code = code;
            if (!fromFirmwareRequest)
            {
                _taskSource = new TaskCompletionSource<CodeResult>();
            }
        }

        /// <summary>
        /// Code item to execute
        /// </summary>
        public Commands.Code Code { get; }

        /// <summary>
        /// Whether the code is already being executed
        /// </summary>
        public bool IsExecuting { get; set; }

        /// <summary>
        /// Whether a code reply has been received for this code indicating if it can be finished
        /// </summary>
        public bool CanFinish { get => _gotEmptyResponse || _result.Count != 0; }

        /// <summary>
        /// Task that is resolve when the code has finished.
        /// May be null if the code is supposed to be a firmware request
        /// </summary>
        public Task<CodeResult> Task { get => _taskSource.Task; }

        /// <summary>
        /// Process a code reply from the firmware
        /// </summary>
        /// <param name="messageType">Message type flags</param>
        /// <param name="reply">Raw code reply</param>
        public void HandleReply(Communication.MessageTypeFlags messageType, string reply)
        {
            reply = reply.Trim();
            if (_lastMessageIncomplete)
            {
                _result[_result.Count - 1].Content += reply;
            }
            else if (reply != "")
            {
                DuetAPI.MessageType type = messageType.HasFlag(Communication.MessageTypeFlags.ErrorMessageFlag) ? DuetAPI.MessageType.Error
                            : messageType.HasFlag(Communication.MessageTypeFlags.WarningMessageFlag) ? DuetAPI.MessageType.Warning
                            : DuetAPI.MessageType.Success;
                _result.Add(new DuetAPI.Message(type, reply));
            }
            else
            {
                _gotEmptyResponse = true;
            }
            _lastMessageIncomplete = messageType.HasFlag(Communication.MessageTypeFlags.PushFlag);
        }

        /// <summary>
        /// Something went wrong while executing this code
        /// </summary>
        /// <param name="e"></param>
        public async void SetException(Exception e)
        {
            if (_taskSource == null)
            {
                _result.Add(new DuetAPI.Message(DuetAPI.MessageType.Error, e.Message));
                using (await Model.Provider.AccessReadWrite())
                {
                    Model.Provider.Get.Messages.AddRange(_result);
                }
            }
            else
            {
                _taskSource.SetException(e);
            }
        }

        /// <summary>
        /// Called to resolve the task because it has finished
        /// </summary>
        public async void SetFinished()
        {
            if (_taskSource == null)
            {
                using (await Model.Provider.AccessReadWrite())
                {
                    Model.Provider.Get.Messages.AddRange(_result);
                }
            }
            else
            {
                _taskSource.SetResult(_result);
            }
        }
    }
}
