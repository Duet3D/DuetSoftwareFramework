using DuetAPI.ObjectModel;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.WriteMessage"/> command
/// </summary>
/// <param name="eventLogger">Internal logger</param>
/// <param name="model">Object model</param>
/// <param name="logger">Logger instance</param>
public sealed class WriteMessage(Utility.EventLogger eventLogger, Model.ObjectModel model, ILogger<WriteMessage> logger) : DuetAPI.Commands.WriteMessage
{
    /// <summary>
    /// Write an arbitrary message
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        LogLevel ??= Type switch
        {
            MessageType.Error => EventLogLevel.Warn,
            MessageType.Warning => EventLogLevel.Warn,
            MessageType.Success => EventLogLevel.Info,
            _ => throw new NotImplementedException()
        };

        Message msg = new(Type, Content);
        await eventLogger.LogAsync(LogLevel.Value, msg);
        if (OutputMessage)
        {
            await model.OutputAsync(msg, cancellationToken);
        }

        if (LogLevel == EventLogLevel.Off && !OutputMessage)
        {
            // If the message is supposed to be written neither to the object model nor to the log file, send it to the DCS log
            logger.LogInformation("{Message}", msg);
        }
    }
}
