using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace SystemTests.Host;

/// <summary>
/// Collects DuetControlServer's log in memory instead of writing it to the console: a passing test
/// stays silent however the runner streams output, and a failing one gets the full debug-level log
/// - more than console logging ever showed - printed by <see cref="BenchFixture"/>'s teardown.
/// </summary>
/// <remarks>
/// The store is static because the host is disposed inside the test method while the outcome is
/// only known in teardown; the fixtures run sequentially, so one store per test is safe
/// </remarks>
internal sealed class CapturedLog : ILoggerProvider
{
    /// <summary>Most lines kept per test; older lines fall off the front</summary>
    private const int MaxLines = 5000;

    private static readonly object _lock = new();
    private static readonly Queue<string> _lines = new();

    /// <summary>Forget the previous test's log</summary>
    public static void Clear()
    {
        lock (_lock)
        {
            _lines.Clear();
        }
    }

    /// <summary>The captured log as one printable block</summary>
    public static string Dump()
    {
        lock (_lock)
        {
            return string.Join('\n', _lines);
        }
    }

    private static void Append(string line)
    {
        lock (_lock)
        {
            _lines.Enqueue(line);
            while (_lines.Count > MaxLines)
            {
                _lines.Dequeue();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            string line = $"{DateTime.Now:HH:mm:ss.fff} {LevelTag(logLevel)} {category}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += $"\n{exception}";
            }
            Append(line);
        }

        private static string LevelTag(LogLevel level) => level switch
        {
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "trce"
        };
    }
}

/// <summary>
/// Base of every bench fixture: starts each test with an empty captured log and prints it when the
/// test failed, so the DuetControlServer log rides with the failure instead of with every run
/// </summary>
public abstract class BenchFixture
{
    [SetUp]
    public void ClearCapturedLog() => CapturedLog.Clear();

    [TearDown]
    public void DumpCapturedLogOnFailure()
    {
        if (TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Failed)
        {
            TestContext.Out.WriteLine("DuetControlServer log of the failed test:");
            TestContext.Out.WriteLine(CapturedLog.Dump());
        }
    }
}
