using System;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DuetEmulation.Dsf;

/// <summary>
/// Writes DuetControlServer's log to a file in the run's directory, beside the emulator's logs
/// </summary>
internal sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    private readonly StreamWriter _writer = new(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
    private bool _disposed;

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

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
            string time = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            string line = $"{time} [{logLevel}] {category}: {formatter(state, exception)}";
            provider.Write(exception is null ? line : $"{line}\n{exception}");
        }
    }
}
