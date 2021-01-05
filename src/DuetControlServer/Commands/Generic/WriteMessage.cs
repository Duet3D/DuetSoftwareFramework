using DuetAPI.ObjectModel;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.WriteMessage"/> command
    /// </summary>
    public sealed class WriteMessage : DuetAPI.Commands.WriteMessage
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Write an arbitrary message
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override async Task Execute()
        {
#pragma warning disable CS0612 // Type or member is obsolete
            if (LogMessage)
            {
                LogLevel = LogLevel.Warn;
            }
#pragma warning restore CS0612 // Type or member is obsolete

            Message msg = new Message(Type, Content);
            await Utility.Logger.Log(LogLevel, msg);
            if (OutputMessage)
            {
                await Model.Provider.Output(msg);
            }
            if (LogLevel == LogLevel.Off && !OutputMessage)
            {
                // If the message is supposed to be written neither to the object model nor to the log file, send it to the DCS log
                _logger.Info(msg.ToString());
            }
        }
    }
}
