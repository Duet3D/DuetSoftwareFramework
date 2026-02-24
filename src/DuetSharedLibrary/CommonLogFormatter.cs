using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using System;
using System.IO;

namespace DuetSharedLibrary;

/// <summary>
/// Custom console formatter that provides a common log output format
/// </summary>
public sealed class CommonLogFormatter : ConsoleFormatter
{
    /// <summary>
    /// Constructor
    /// </summary>
    public CommonLogFormatter() : base(nameof(CommonLogFormatter))
    {
        // Disable console output buffering to ensure journalctl -f works correctly
        // When stdout is not a terminal (e.g., systemd service), it's fully buffered by default
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
    }

    /// <summary>
    /// Write a log entry
    /// </summary>
    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        string? message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (logEntry.Exception == null && message == null)
        {
            return;
        }

        // Get log level name and color
        string levelName = GetLogLevelString(logEntry.LogLevel);
        string colorCode = GetColorCode(logEntry.LogLevel);

        // Get category name (logger name)
        string categoryName = logEntry.Category;

        // Format: [level] [logger:] message
        textWriter.Write('[');
        textWriter.Write(colorCode);
        textWriter.Write(levelName);
        textWriter.Write("\x1b[0m");  // Reset color
        textWriter.Write("] ");

        // Only include category if it doesn't contain '.' and doesn't end with '.g'
        bool includeCategory = !categoryName.Contains('.') && !categoryName.EndsWith(".g");
        if (includeCategory)
        {
            textWriter.Write(categoryName);
            textWriter.Write(": ");
        }

        // Write message
        if (!string.IsNullOrEmpty(message))
        {
            textWriter.Write(message);
        }

        // Write exception if present and different from message
        if (logEntry.Exception != null)
        {
            string exceptionString = logEntry.Exception.ToString();
            if (message != exceptionString)
            {
                textWriter.WriteLine();
                
                // Color the exception based on log level
                string exceptionColor = logEntry.LogLevel >= LogLevel.Error ? colorCode : "";
                if (!string.IsNullOrEmpty(exceptionColor))
                {
                    textWriter.Write(exceptionColor);
                }
                
                textWriter.Write("   ");
                textWriter.Write(exceptionString);
                
                if (!string.IsNullOrEmpty(exceptionColor))
                {
                    textWriter.Write("\x1b[0m");  // Reset color
                }
            }
        }

        textWriter.WriteLine();
    }

    /// <summary>
    /// Get the ANSI color code for the log level
    /// </summary>
    private static string GetColorCode(LogLevel logLevel)
    {
        return logLevel switch
        {
            LogLevel.Trace => "\x1b[90m",           // Gray
            LogLevel.Debug => "\x1b[2;37m",         // Dim Gray
            LogLevel.Information => "\x1b[32m",     // Green
            LogLevel.Warning => "\x1b[33m",         // Yellow
            LogLevel.Error => "\x1b[31m",           // Red
            LogLevel.Critical => "\x1b[1m\x1b[31m", // Bold Red
            _ => "\x1b[0m"                          // Default/Reset
        };
    }

    /// <summary>
    /// Get the string representation of the log level
    /// </summary>
    private static string GetLogLevelString(LogLevel logLevel)
    {
        return logLevel switch
        {
            LogLevel.Trace => "trace",
            LogLevel.Debug => "debug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warning",
            LogLevel.Error => "error",
            LogLevel.Critical => "fatal",
            _ => logLevel.ToString().ToLowerInvariant()
        };
    }
}

/// <summary>
/// Options for the common log formatter
/// </summary>
public sealed class CommonLogFormatterOptions : ConsoleFormatterOptions
{
    // No additional options needed currently, but can be extended if needed
}
