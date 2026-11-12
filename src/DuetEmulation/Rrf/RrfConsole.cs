using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetEmulation.Emulator;

namespace DuetEmulation.Rrf;

/// <summary>
/// The G-code console of a main board running RepRapFirmware standalone: a UART whose output Renode
/// writes to a file, opened by <c>M575 P3 S2</c> in config.g
/// </summary>
/// <remarks>
/// Each line sent is followed by an <c>echo</c> of a unique marker on the same channel. The firmware
/// runs that only once the line has finished, so its output marks the end of the line's reply. Several
/// lines may be in flight; their replies are told apart by their markers, in the order they were sent
/// </remarks>
public sealed class RrfConsole
{
    /// <summary>The config.g line that opens the console: Aux2 in raw mode</summary>
    public const string OpenCommand = "M575 P3 S2 B57600";

    private readonly EmulatedSystem _system;
    private readonly EmulatedBoard _board;
    private readonly string _uart;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _transcript = new();
    private readonly StringBuilder _unclaimed = new();
    private readonly Queue<(string Marker, TaskCompletionSource<string> Reply)> _pending = new();
    private long _read;
    private int _marker;

    /// <summary>The file the UART's output goes to</summary>
    public string OutputPath { get; }

    /// <summary>Everything the firmware has printed on the console so far</summary>
    public string Transcript
    {
        get
        {
            lock (_lock)
            {
                return _transcript.ToString();
            }
        }
    }

    private RrfConsole(EmulatedSystem system, EmulatedBoard board, string uart, string outputPath)
    {
        _system = system;
        _board = board;
        _uart = uart;
        OutputPath = outputPath;
    }

    /// <summary>Connect the main board's console UART to a file</summary>
    public static async Task<RrfConsole> OpenAsync(EmulatedSystem system, CancellationToken cancellationToken = default)
    {
        EmulatedBoard board = system.Main;
        string uart = board.Type.ConsoleUart ?? throw new InvalidOperationException($"{board.Type.Name} has no console UART");
        string path = Path.Combine(system.WorkDirectory, $"{board.Machine}-console.txt");
        await system.ExecuteAsync(board, $"{uart} CreateFileBackend {MonitorSyntax.PathArgument(path)} true", cancellationToken);
        return new RrfConsole(system, board, uart, path);
    }

    /// <summary>
    /// Send one line of G-code without waiting for it, and return its reply, which completes once the
    /// firmware has finished the line and <see cref="Poll"/> has seen its marker
    /// </summary>
    public async Task<Task<string>> SendAsync(string line, CancellationToken cancellationToken = default)
    {
        if (line.Contains('\n'))
        {
            throw new ArgumentException("One line at a time", nameof(line));
        }

        TaskCompletionSource<string> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // A line and its marker go out together, so that lines sent at once cannot interleave
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            string marker;
            lock (_lock)
            {
                ReadNewOutput();
                marker = $"<<{++_marker}>>";
                _pending.Enqueue((marker, reply));
            }
            await WriteLineAsync(line, cancellationToken);
            await WriteLineAsync($"echo \"{marker}\"", cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
        return reply.Task;
    }

    /// <summary>Read what the firmware has printed and complete the replies whose markers it has printed</summary>
    public void Poll()
    {
        lock (_lock)
        {
            ReadNewOutput();
            while (_pending.TryPeek(out (string Marker, TaskCompletionSource<string> Reply) next))
            {
                string text = _unclaimed.ToString();
                int end = text.IndexOf(next.Marker, StringComparison.Ordinal);
                if (end < 0)
                {
                    // The firmware runs lines in order, so a later line's marker means this line never
                    // arrived, as happens to lines sent before config.g has opened the console
                    if (_pending.Skip(1).Any(p => text.Contains(p.Marker, StringComparison.Ordinal)))
                    {
                        _pending.Dequeue();
                        next.Reply.SetException(new InvalidOperationException("The firmware did not receive the line"));
                        continue;
                    }
                    break;
                }
                int rest = end + next.Marker.Length;
                while (rest < text.Length && text[rest] is '\r' or '\n')
                {
                    rest++;
                }
                _unclaimed.Remove(0, rest);
                _pending.Dequeue();
                next.Reply.SetResult(text[..end].Trim());
            }
        }
    }

    /// <summary>Send one line of G-code and return what the firmware printed in reply</summary>
    /// <exception cref="TimeoutException">No reply within <paramref name="timeout"/> of emulated time</exception>
    public async Task<string> CommandAsync(string line, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Task<string> reply = await SendAsync(line, cancellationToken);
        return await WaitAsync(reply, line, timeout, cancellationToken);
    }

    /// <summary>Let emulated time pass until a reply is complete</summary>
    /// <exception cref="TimeoutException">No reply within <paramref name="timeout"/> of emulated time</exception>
    public async Task<string> WaitAsync(Task<string> reply, string line, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        TimeSpan start = _system.Time;
        Poll();
        while (!reply.IsCompleted)
        {
            if (_system.Time - start >= timeout)
            {
                throw new TimeoutException($"No reply to \"{line}\" within {timeout.TotalSeconds:0.###}s of emulated time");
            }
            await _system.RunForAsync(Bench.DefaultPollInterval, cancellationToken);
            Poll();
        }
        return await reply;
    }

    private Task WriteLineAsync(string line, CancellationToken cancellationToken) =>
        _system.ExecuteAsync(_board, $"{_uart} WriteLine {MonitorSyntax.Quote(line)} LF", cancellationToken);

    private void ReadNewOutput()
    {
        if (!File.Exists(OutputPath))
        {
            return;
        }
        byte[] data;
        using (FileStream stream = new(OutputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (stream.Length <= _read)
            {
                return;
            }
            stream.Seek(_read, SeekOrigin.Begin);
            data = new byte[stream.Length - _read];
            stream.ReadExactly(data);
        }
        _read += data.Length;
        // The decoder keeps a character split across reads until the rest of it arrives
        char[] chars = new char[_decoder.GetCharCount(data, 0, data.Length)];
        string text = new(chars, 0, _decoder.GetChars(data, 0, data.Length, chars, 0));
        _transcript.Append(text);
        _unclaimed.Append(text);
    }
}
