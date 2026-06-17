using System;
using System.Collections.Concurrent;
using DuetAPI.ObjectModel;
using Microsoft.Extensions.Logging;

namespace DuetControlServer.Utility;

/// <summary>
/// Logger provider used to optionally output DCS log messages as generic messages
/// </summary>
public sealed class MessageLoggerProvider : ILoggerProvider
{
    private readonly Model.ObjectModel _model;
    private readonly ConcurrentDictionary<string, MessageLogger> _loggers = new();

    /// <summary>
    /// Constructor of this class
    /// </summary>
    /// <param name="model">Object model</param>
    /// <param name="minimumLevel">Minimum log level</param>
    public MessageLoggerProvider(Model.ObjectModel model, LogLevel minimumLevel)
    {
        _model = model;
        MinimumLevel = minimumLevel;
    }

    /// <summary>
    /// Minimum log level
    /// </summary>
    public LogLevel MinimumLevel { get; set; }

    /// <summary>
    /// Whether messages are output to the object model. The logger factory offers no way to remove
    /// a registered provider again, so M111 O0 disables the output via this flag instead
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Create a logger
    /// </summary>
    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new MessageLogger(name, _model, this));
    }

    /// <summary>
    /// Dispose of this provider
    /// </summary>
    public void Dispose()
    {
        _loggers.Clear();
    }
}

/// <summary>
/// Logger that outputs messages to the object model
/// </summary>
public sealed class MessageLogger : ILogger
{
    private readonly string _categoryName;
    private readonly Model.ObjectModel _model;
    private readonly MessageLoggerProvider _provider;

    /// <summary>
    /// Constructor
    /// </summary>
    public MessageLogger(string categoryName, Model.ObjectModel model, MessageLoggerProvider provider)
    {
        _categoryName = categoryName;
        _model = model;
        _provider = provider;
    }

    /// <summary>
    /// Begin a scope
    /// </summary>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <summary>
    /// Check if log level is enabled
    /// </summary>
    public bool IsEnabled(LogLevel logLevel) => _provider.Enabled && logLevel >= _provider.MinimumLevel;

    /// <summary>
    /// Log a message
    /// </summary>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        // Determine message type
        MessageType messageType = logLevel switch
        {
            LogLevel.Error or LogLevel.Critical => MessageType.Error,
            LogLevel.Warning => MessageType.Warning,
            _ => MessageType.Success
        };

        // Format the message
        string? message = formatter(state, exception);
        if (exception != null && message != exception.ToString())
        {
            message += Environment.NewLine + "   " + exception.ToString();
        }

        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        // Add logger name prefix if it doesn't contain '.'
        if (!_categoryName.Contains('.'))
        {
            message = $"{_categoryName}: {message}";
        }

        // Add message to object model
        _ = _model.OutputAsync(new Message(messageType, message));
    }
} 
