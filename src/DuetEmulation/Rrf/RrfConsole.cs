using System;
using System.IO;
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
/// runs that only once the line has finished, so its output marks the end of the line's reply
/// </remarks>
public sealed class RrfConsole
{
    /// <summary>How far emulated time advances between checks for a reply</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>The config.g line that opens the console: Aux2 in raw mode</summary>
    public const string OpenCommand = "M575 P3 S2 B57600";

    private readonly EmulatedSystem _system;
    private readonly EmulatedBoard _board;
    private readonly string _uart;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly StringBuilder _transcript = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private long _read;
    private int _marker;

    /// <summary>The file the UART's output goes to</summary>
    public string OutputPath { get; }

    /// <summary>Everything the firmware has printed on the console so far</summary>
    public string Transcript
    {
        get
        {
            lock (_transcript)
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

    /// <summary>Send one line of G-code and return what the firmware printed in reply</summary>
    /// <exception cref="TimeoutException">No reply within <paramref name="timeout"/> of emulated time</exception>
    public async Task<string> CommandAsync(string line, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (line.Contains('\n'))
        {
            throw new ArgumentException("One line at a time", nameof(line));
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            ReadNewOutput();
            string marker = $"<<{++_marker}>>";
            await WriteLineAsync(line, cancellationToken);
            await WriteLineAsync($"echo \"{marker}\"", cancellationToken);

            StringBuilder received = new();
            TimeSpan start = _system.Time;
            while (true)
            {
                received.Append(ReadNewOutput());
                string text = received.ToString();
                int end = text.IndexOf(marker, StringComparison.Ordinal);
                if (end >= 0)
                {
                    return text[..end].Trim();
                }
                if (_system.Time - start >= timeout)
                {
                    throw new TimeoutException($"No reply to \"{line}\" within {timeout.TotalSeconds:0.###}s of emulated time; got \"{text}\"");
                }
                await _system.RunForAsync(PollInterval, cancellationToken);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private Task WriteLineAsync(string line, CancellationToken cancellationToken) =>
        _system.ExecuteAsync(_board, $"{_uart} WriteLine {MonitorSyntax.Quote(line)} LF", cancellationToken);

    private string ReadNewOutput()
    {
        if (!File.Exists(OutputPath))
        {
            return string.Empty;
        }
        using FileStream stream = new(OutputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length <= _read)
        {
            return string.Empty;
        }
        stream.Seek(_read, SeekOrigin.Begin);
        byte[] data = new byte[stream.Length - _read];
        stream.ReadExactly(data);
        _read += data.Length;
        // The decoder keeps a character split across reads until the rest of it arrives
        char[] chars = new char[_decoder.GetCharCount(data, 0, data.Length)];
        string text = new(chars, 0, _decoder.GetChars(data, 0, data.Length, chars, 0));
        lock (_transcript)
        {
            _transcript.Append(text);
        }
        return text;
    }
}
