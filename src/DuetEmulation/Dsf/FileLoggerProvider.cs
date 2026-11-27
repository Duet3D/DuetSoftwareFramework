using System;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DuetEmulation.Dsf;

/// <summary>
/// Writes DuetControlServer's log to a file in the run's directory, beside the emulator's logs, each
/// line stamped with the emulated time it was written at, in seconds as captures give it, so that the
/// log lines up with a capture and two runs' logs can be compared line by line
/// </summary>
/// <param name="path">The log file</param>
/// <param name="now">The emulated time DuetControlServer's clock reads</param>
internal sealed class FileLoggerProvider(string path, Func<TimeSpan> now) : ILoggerProvider
{
    private readonly StreamWriter _writer = new(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
    private bool _disposed;

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    private Func<TimeSpan> Now => now;

    private void Write(string line)
    {
        lock (_writer)
        {
            if (!_disposed)
            {
                _writer.WriteLine(line);
            }
        }
    }

    public void Dispose()
    {
        lock (_writer)
        {
            _disposed = true;
            _writer.Dispose();
        }
    }

    private sealed class Logger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            string time = provider.Now().TotalSeconds.ToString("0.000000", CultureInfo.InvariantCulture);
            string line = $"{time} [{logLevel}] {category}: {formatter(state, exception)}";
            provider.Write(exception is null ? line : $"{line}\n{exception}");
        }
    }
}
