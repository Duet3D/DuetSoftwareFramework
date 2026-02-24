using Microsoft.Extensions.Logging;

namespace DuetControlServer.Utility;

/// <summary>
/// Helper methods for parsing log level strings
/// </summary>
public static class LogLevelHelper
{
    /// <summary>
    /// Valid log level names shown in help/error messages (lowercase short forms)
    /// </summary>
    public const string ValidLogLevels = "trace, debug, info, warn, error, fatal, off";

    /// <summary>
    /// Try to parse a log level string, accepting both canonical .NET names and common short aliases
    /// </summary>
    /// <param name="value">String to parse</param>
    /// <param name="logLevel">Parsed log level on success</param>
    /// <returns>True if the value was recognised</returns>
    public static bool TryParseLogLevel(string value, out LogLevel logLevel)
    {
        logLevel = value.ToLowerInvariant() switch
        {
            "trace"                 => LogLevel.Trace,
            "debug"                 => LogLevel.Debug,
            "info" or "information" => LogLevel.Information,
            "warn" or "warning"     => LogLevel.Warning,
            "error"                 => LogLevel.Error,
            "fatal" or "critical"   => LogLevel.Critical,
            "off"  or "none"        => LogLevel.None,
            _                       => (LogLevel)(-2)     // sentinel for "not recognised"
        };

        if ((int)logLevel == -2)
        {
            logLevel = LogLevel.Information;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Parse a log level string, returning <paramref name="defaultLevel"/> if the value is not recognised
    /// </summary>
    /// <param name="value">String to parse</param>
    /// <param name="defaultLevel">Value to return when parsing fails</param>
    /// <returns>Parsed or default log level</returns>
    public static LogLevel ParseLogLevel(string value, LogLevel defaultLevel = LogLevel.Information)
        => TryParseLogLevel(value, out LogLevel level) ? level : defaultLevel;
}
