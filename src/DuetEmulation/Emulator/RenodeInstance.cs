using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DuetEmulation.Emulator;

/// <summary>
/// A headless Renode process, driven through its robot server: the XML-RPC interface Renode's own test
/// runner uses, which reports each monitor command's success or failure explicitly
/// </summary>
/// <remarks>
/// Commands are serialised: one runs at a time, in the order they were issued. <c>emulation RunFor</c>
/// returns once the emulated time has passed, so nothing runs between commands unless the emulation
/// has been started free-running
/// </remarks>
public sealed class RenodeInstance : IAsyncDisposable
{
    /// <summary>How long Renode may take to open its robot server</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long one command may take before Renode is presumed hung</summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

    private readonly Process _process;
    private readonly LogSink _log;
    private readonly XmlRpcClient _client;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    /// <summary>The file Renode's own output goes to</summary>
    public string LogPath { get; }

    private RenodeInstance(Process process, LogSink log, XmlRpcClient client, string logPath)
    {
        _process = process;
        _log = log;
        _client = client;
        LogPath = logPath;
    }

    /// <summary>The renode executable: <c>$RENODE</c>, then <c>/opt/renode/renode</c>, then the PATH</summary>
    /// <exception cref="FileNotFoundException">Renode is not installed</exception>
    public static string FindExecutable()
    {
        List<string> candidates = [];
        if (Environment.GetEnvironmentVariable("RENODE") is { Length: > 0 } fromEnvironment)
        {
            candidates.Add(fromEnvironment);
        }
        candidates.Add("/opt/renode/renode");
        if (FindOnPath("renode") is string onPath)
        {
            candidates.Add(onPath);
        }
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException("Renode not found: install it in /opt/renode (see .devcontainer/install-renode.sh) or set $RENODE");
    }

    /// <summary>Start Renode, with its output going to <paramref name="logPath"/></summary>
    /// <exception cref="RenodeException">Renode did not start</exception>
    public static async Task<RenodeInstance> StartAsync(string logPath, CancellationToken cancellationToken = default)
    {
        string executable = FindExecutable();
        // The port is free when it is chosen but another process may take it first, so try a few
        const int attempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            int port = FreePort();
            // setpriv has the kernel kill Renode when the thread that started it dies, which is the
            // launcher's, which lives as long as this process: a test run that is killed does not leave
            // emulators running
            string? setpriv = FindOnPath("setpriv");
            ProcessStartInfo startInfo = new(setpriv ?? executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false
            };
            if (setpriv is not null)
            {
                startInfo.ArgumentList.Add("--pdeathsig");
                startInfo.ArgumentList.Add("KILL");
                startInfo.ArgumentList.Add(executable);
            }
            foreach (string argument in new[] { "--disable-gui", "--plain", "--hide-log", "--port", "-1", "--robot-server-port", port.ToString() })
            {
                startInfo.ArgumentList.Add(argument);
            }
            // Renode is a .NET program, whose files are otherwise locked against the bench reading them
            // while it writes (the console, the CAN and I/O traces)
            startInfo.Environment["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = "1";

            Process process = await ProcessLauncher.StartAsync(startInfo);
            LogSink log = new(logPath, append: attempt > 1);
            process.OutputDataReceived += (_, e) => log.Write(e.Data);
            process.ErrorDataReceived += (_, e) => log.Write(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            XmlRpcClient client = new(new Uri($"http://127.0.0.1:{port}/"), CommandTimeout);
            bool started = false;
            try
            {
                started = await WaitForServerAsync(process, client, cancellationToken);
            }
            finally
            {
                if (!started)
                {
                    client.Dispose();
                    Kill(process);
                    log.Dispose();
                }
            }
            if (started)
            {
                return new RenodeInstance(process, log, client, logPath);
            }
            if (attempt == attempts)
            {
                throw new RenodeException($"Renode did not open its robot server; see {logPath}");
            }
        }
    }

    private static async Task<bool> WaitForServerAsync(Process process, XmlRpcClient client, CancellationToken cancellationToken)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < StartTimeout)
        {
            if (process.HasExited)
            {
                return false;
            }
            try
            {
                using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(TimeSpan.FromSeconds(5));
                await client.CallAsync("get_keyword_names", [], attempt.Token);
                return true;
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested &&
                                      e is HttpRequestException or OperationCanceledException or System.Xml.XmlException or XmlRpcFaultException)
            {
                // Not up yet, or something else answered on the port
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
        }
        return false;
    }

    private static string? FindOnPath(string name)
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(directory, name);
            if (directory.Length > 0 && File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Run one monitor command and return what it printed</summary>
    /// <param name="command">The command, as typed at the monitor</param>
    /// <param name="machine">The machine to run it on, or null for the emulation as a whole</param>
    /// <param name="cancellationToken">Cancels waiting for the command</param>
    /// <exception cref="RenodeCommandException">The command failed</exception>
    public async Task<string> ExecuteAsync(string command, string? machine = null, CancellationToken cancellationToken = default)
    {
        if (command.Contains('\n'))
        {
            throw new ArgumentException("One command at a time", nameof(command));
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            List<object> arguments = machine is null ? [command] : [command, machine];
            object? result;
            try
            {
                result = await _client.CallAsync("run_keyword", ["ExecuteCommand", arguments], cancellationToken);
            }
            catch (HttpRequestException e) when (_process.HasExited)
            {
                throw new RenodeException($"Renode exited with code {_process.ExitCode}; see {LogPath}", e);
            }

            if (result is not Dictionary<string, object?> keywordResult)
            {
                throw new RenodeException($"Unexpected reply to {command}: {result}");
            }
            if (keywordResult.GetValueOrDefault("status") as string != "PASS")
            {
                throw new RenodeCommandException(command, (keywordResult.GetValueOrDefault("error") as string ?? "failed").Trim());
            }
            return (keywordResult.GetValueOrDefault("return") as string ?? string.Empty).Replace("\r", string.Empty).Trim();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Run a command that prints a number, and return the number</summary>
    public async Task<ulong> ExecuteForNumberAsync(string command, string? machine = null, CancellationToken cancellationToken = default)
    {
        string output = await ExecuteAsync(command, machine, cancellationToken);
        if (MonitorSyntax.TryParseNumber(output, out ulong value))
        {
            return value;
        }
        throw new RenodeCommandException(command, $"printed \"{output}\", not a number");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // A command that hangs holds the lock; Renode is killed rather than waited for
        if (!await _lock.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            Kill(_process);
            await _lock.WaitAsync();
        }
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (!_process.HasExited)
            {
                try
                {
                    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
                    await _client.CallAsync("run_keyword", ["ExecuteCommand", new List<object> { "quit" }], timeout.Token);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or XmlRpcFaultException)
                {
                    // Quitting closes the connection before it answers
                }
                try
                {
                    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
                    await _process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    Kill(_process);
                }
            }
            _client.Dispose();
            _process.Dispose();
            _log.Dispose();
        }
        finally
        {
            _lock.Release();
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        catch (InvalidOperationException)
        {
            // Already gone
        }
    }
}

/// <summary>Where Renode's output goes: a file that lines may still arrive for after it is closed</summary>
internal sealed class LogSink(string path, bool append) : IDisposable
{
    private readonly StreamWriter _writer = new(path, append) { AutoFlush = true };
    private bool _closed;

    public void Write(string? line)
    {
        lock (_writer)
        {
            if (line is not null && !_closed)
            {
                _writer.WriteLine(line);
            }
        }
    }

    public void Dispose()
    {
        lock (_writer)
        {
            _closed = true;
            _writer.Dispose();
        }
    }
}

/// <summary>Renode failed or could not be reached</summary>
public class RenodeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A monitor command failed</summary>
public sealed class RenodeCommandException(string command, string error)
    : RenodeException($"{command}: {error}")
{
    /// <summary>The command that failed</summary>
    public string Command { get; } = command;

    /// <summary>What Renode said about it</summary>
    public string Error { get; } = error;
}
