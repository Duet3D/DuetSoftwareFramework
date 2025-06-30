using DuetAPI.ObjectModel;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.WriteMessage"/> command
/// </summary>
/// <param name="dsfLogger">Internal logger</param>
/// <param name="model">Object model</param>
public sealed class WriteMessage(Utility.Logger dsfLogger, Model.ObjectModel model) : DuetAPI.Commands.WriteMessage
{
    /// <summary>
    /// Logger instance
    /// </summary>
    private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Write an arbitrary message
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        LogLevel ??= Type switch
        {
            MessageType.Error => DuetAPI.ObjectModel.LogLevel.Warn,
            MessageType.Warning => DuetAPI.ObjectModel.LogLevel.Warn,
            MessageType.Success => DuetAPI.ObjectModel.LogLevel.Info,
            _ => throw new NotImplementedException()
        };

#pragma warning disable CS0618 // Type or member is obsolete
        if (LogMessage)
        {
            LogLevel = DuetAPI.ObjectModel.LogLevel.Warn;
        }
#pragma warning restore CS0618 // Type or member is obsolete

        Message msg = new(Type, Content);
        await dsfLogger.LogAsync(LogLevel.Value, msg);
        if (OutputMessage)
        {
            await model.OutputAsync(msg, cancellationToken);
        }

        if (LogLevel == DuetAPI.ObjectModel.LogLevel.Off && !OutputMessage)
        {
            // If the message is supposed to be written neither to the object model nor to the log file, send it to the DCS log
            _logger.Info(msg.ToString());
        }
    }
}
