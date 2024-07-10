using System;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NLog;
using NLog.Targets;

namespace DuetControlServer.Utility
{
    [Target("MessageLogTarget")] 
    public sealed class MessageLogTarget : AsyncTaskTarget
    { 
        /// <summary>
        /// Constructor of this class
        /// </summary>
        public MessageLogTarget()
        {
            IncludeEventProperties = true; // Include LogEvent Properties by default
        }
 

        /// <summary>
        /// Called to handle incoming log events
        /// </summary>
        /// <param name="logEvent">Event to log</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>Asynchronous task</returns>
        protected override async Task WriteAsyncTask(LogEventInfo logEvent, CancellationToken token)
        {
            // Determine message type
            MessageType messageType = MessageType.Success;
            if (logEvent.Level == NLog.LogLevel.Error)
            {
                messageType = MessageType.Error;
            }
            else if (logEvent.Level == NLog.LogLevel.Warn)
            {
                messageType = MessageType.Warning;
            }

            // Render log message
            string logMessage = RenderLogEvent(@"${message}${onexception:when='${message}'!='${exception:format=ToString}'):${newline}   ${exception:format=ToString}}", logEvent);
            if (!logEvent.LoggerName.Contains('.'))
            {
                logMessage = $"{logEvent.LoggerName}: {logMessage}";
            }

            // Output it
            using (await Model.Provider.AccessReadWriteAsync(token))
            {
                Model.Provider.Get.Messages.Add(new Message(messageType, logMessage));
            }
        } 
    } 
}