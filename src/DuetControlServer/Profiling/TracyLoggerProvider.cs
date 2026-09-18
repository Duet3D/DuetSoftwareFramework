using System;
using Microsoft.Extensions.Logging;

namespace DuetControlServer.Profiling;

/// <summary>
/// Logging provider that puts DuetControlServer's log messages on the Tracy timeline
/// </summary>
/// <remarks>
/// Registered from Program.cs in a profiling build. Tracy shows each message as a marker on the
/// thread that logged it, which is what ties a log line to the zones running around it, and lists
/// them all in its message window. The filters set up alongside the console provider apply here
/// too, so the runtime log level (M111) decides what reaches Tracy as well.
/// </remarks>
internal sealed class TracyLoggerProvider : ILoggerProvider
{
    /// <summary>
    /// Create a logger for a category
    /// </summary>
    /// <param name="categoryName">Category to log under</param>
    /// <returns>Logger writing to Tracy</returns>
    public ILogger CreateLogger(string categoryName) => new TracyLogger(categoryName);

    /// <summary>
    /// Dispose this provider
    /// </summary>
    /// <remarks>
    /// The loggers hold nothing of their own; the Tracy client is shut down by the process exiting.
    /// </remarks>
    public void Dispose()
    {
        // Nothing to dispose
    }
}

/// <summary>
/// Logger reporting messages of one category to Tracy
/// </summary>
/// <param name="category">Category being logged</param>
internal sealed class TracyLogger(string category) : ILogger
{
    /// <summary>
    /// Colour of a message that is only of interest when following the code in detail
    /// </summary>
    private const uint TraceColour = 0x808080;

    /// <summary>
    /// Colour of an ordinary progress message
    /// </summary>
    private const uint InformationColour = 0xF0F0F0;

    /// <summary>
    /// Colour of a message reporting something unexpected but survivable
    /// </summary>
    private const uint WarningColour = 0xFFC800;

    /// <summary>
    /// Colour of a message reporting a failure
    /// </summary>
    private const uint ErrorColour = 0xFF4040;

    /// <summary>
    /// Category shown in front of the message
    /// </summary>
    /// <remarks>
    /// Just the class name. The full category is what the console shows, but on a timeline the
    /// enclosing zones already say which subsystem a message came from, and the namespace in front
    /// of every message would only push the message itself out of view.
    /// </remarks>
    private readonly string _category = category[(category.LastIndexOf('.') + 1)..];

    /// <summary>
    /// Begin a logical operation scope
    /// </summary>
    /// <typeparam name="TState">Type of the state</typeparam>
    /// <param name="state">State to begin the scope with</param>
    /// <returns>Scope that does nothing</returns>
    /// <remarks>
    /// Zones are what nest on a Tracy timeline, and they come from the weaver rather than from
    /// logging scopes, so a scope has nothing to add here.
    /// </remarks>
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    /// <summary>
    /// Check whether messages of a given level are reported
    /// </summary>
    /// <param name="logLevel">Level to check</param>
    /// <returns>Whether the level is logged</returns>
    /// <remarks>
    /// Only what is being captured is worth formatting, and while no Tracy GUI is connected an
    /// on-demand client records nothing at all.
    /// </remarks>
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && TracyProfiler.Connected;

    /// <summary>
    /// Report a message to Tracy
    /// </summary>
    /// <typeparam name="TState">Type of the state</typeparam>
    /// <param name="logLevel">Level of the message</param>
    /// <param name="eventId">Event that produced it</param>
    /// <param name="state">State to format</param>
    /// <param name="exception">Exception that came with it, if any</param>
    /// <param name="formatter">Formatter turning the state into a message</param>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        string message = formatter(state, exception);
        if (exception is not null)
        {
            // The type and message only: a Tracy message is a line on a timeline, and a stack trace
            // in one would bury the messages around it
            message = $"{message} -> {exception.GetType().Name}: {exception.Message}";
        }

        TracyProfiler.Message($"{_category}: {message}", ColourFor(logLevel));
    }

    /// <summary>
    /// Get the colour to show a message of a given level in
    /// </summary>
    /// <param name="logLevel">Level of the message</param>
    /// <returns>Colour as 0xRRGGBB</returns>
    private static uint ColourFor(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Trace or LogLevel.Debug => TraceColour,
        LogLevel.Warning => WarningColour,
        LogLevel.Error or LogLevel.Critical => ErrorColour,
        _ => InformationColour
    };
}

/// <summary>
/// Scope that does nothing, handed out by <see cref="TracyLogger.BeginScope"/>
/// </summary>
internal sealed class NullScope : IDisposable
{
    /// <summary>
    /// The one instance there needs to be
    /// </summary>
    internal static NullScope Instance { get; } = new();

    /// <summary>
    /// Constructor
    /// </summary>
    private NullScope()
    {
        // Nothing to do
    }

    /// <summary>
    /// End the scope
    /// </summary>
    public void Dispose()
    {
        // Nothing to do
    }
}
