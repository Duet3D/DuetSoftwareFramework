using DuetAPI.Commands;
using Microsoft.Extensions.Logging;

namespace DuetControlServer.Utility;

/// <summary>
/// Source-generated logger messages for hot code paths so that disabled log levels
/// do not allocate parameter arrays or box values
/// </summary>
internal static partial class LogMessages
{
    [LoggerMessage(Level = LogLevel.Trace, Message = "Read code {Code}")]
    internal static partial void LogReadCode(this ILogger logger, Commands.Code code);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Restarting {Keyword} block, iterations = {Iterations}")]
    internal static partial void LogRestartingBlock(this ILogger logger, KeywordType keyword, int iterations);

    [LoggerMessage(Level = LogLevel.Trace, Message = "IPC#{Id}: Sending success response")]
    internal static partial void LogSendingSuccessResponse(this ILogger logger, int id);
}
