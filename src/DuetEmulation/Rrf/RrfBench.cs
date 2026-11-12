using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetEmulation.Boards;
using DuetEmulation.Storage;

namespace DuetEmulation.Rrf;

/// <summary>
/// A main board running RepRapFirmware standalone, with its expansion boards: codes go in through its
/// console and the object model comes back through M409
/// </summary>
/// <remarks>
/// Nothing runs between calls, so a test is a deterministic sequence: emulated time passes only while a
/// call waits for something
/// </remarks>
public sealed class RrfBench : Bench
{
    /// <summary>The line config.g starts with so that the bench can send codes</summary>
    public static string ConsolePreamble => RrfConsole.OpenCommand + "\n";

    private readonly RrfConsole _console;

    /// <summary>The console the bench sends codes through</summary>
    public RrfConsole Console => _console;

    private RrfBench(EmulatedSystem system, RrfConsole console) : base(system) => _console = console;

    /// <summary>
    /// Create the boards, with <paramref name="config"/> as config.g after the line that opens the
    /// console, and boot them
    /// </summary>
    /// <param name="expansionBoards">The expansion boards, by CAN address</param>
    /// <param name="config">The rest of config.g</param>
    /// <param name="workDirectory">Where the run's files go</param>
    /// <param name="files">Further files for the SD card, by path on the card</param>
    /// <param name="cancellationToken">Cancels starting</param>
    public static async Task<RrfBench> StartAsync(IReadOnlyDictionary<int, BoardType> expansionBoards, string config, string workDirectory,
                                                  IReadOnlyDictionary<string, string>? files = null, CancellationToken cancellationToken = default)
    {
        Dictionary<string, byte[]> sdCard = new() { ["sys/config.g"] = SdCardImage.Text(ConsolePreamble + config) };
        foreach ((string path, string contents) in files ?? new Dictionary<string, string>())
        {
            sdCard[path] = SdCardImage.Text(contents);
        }

        EmulatedSystem system = await EmulatedSystem.CreateAsync(new SystemOptions
        {
            MainBoard = BoardTypes.Mb6hcStandalone,
            ExpansionBoards = expansionBoards,
            SdCardFiles = sdCard,
            WorkDirectory = workDirectory
        }, cancellationToken);
        try
        {
            RrfBench bench = new(system, await RrfConsole.OpenAsync(system, cancellationToken));
            await bench.BootAsync(cancellationToken: cancellationToken);
            return bench;
        }
        catch
        {
            await system.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc/>
    protected override async Task StartStackAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        // The console opens once config.g has run, until when nothing answers
        TimeSpan start = System.Time;
        while (true)
        {
            try
            {
                await _console.CommandAsync("M115", TimeSpan.FromMilliseconds(500), cancellationToken);
                return;
            }
            catch (TimeoutException) when (System.Time - start < timeout)
            {
            }
        }
    }

    /// <inheritdoc/>
    public override async Task<CodeReply> SendCodeAsync(string code, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        ParseReply(await _console.CommandAsync(code, timeout ?? DefaultCodeTimeout, cancellationToken));

    /// <summary>A console reply as text and severity: RepRapFirmware prefixes errors and warnings with their severity</summary>
    public static CodeReply ParseReply(string reply)
    {
        const string errorPrefix = "Error: ";
        const string warningPrefix = "Warning: ";
        string[] lines = reply.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        MessageType severity = MessageType.Success;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith(errorPrefix, StringComparison.Ordinal))
            {
                lines[i] = lines[i][errorPrefix.Length..];
                severity = MessageType.Error;
            }
            else if (lines[i].StartsWith(warningPrefix, StringComparison.Ordinal))
            {
                lines[i] = lines[i][warningPrefix.Length..];
                if (severity == MessageType.Success)
                {
                    severity = MessageType.Warning;
                }
            }
        }
        return new CodeReply(string.Join('\n', lines).Trim(), severity);
    }

    /// <inheritdoc/>
    public override async Task<JsonNode?> GetObjectModelAsync(string key = "", CancellationToken cancellationToken = default)
    {
        // d99: every level; v: verbose (every field); n: include nulls
        string command = $"M409 K\"{key}\" F\"d99vn\"";
        string reply = await _console.CommandAsync(command, DefaultCodeTimeout, cancellationToken);
        int start = reply.IndexOf('{');
        if (start < 0)
        {
            throw new InvalidOperationException($"{command} returned \"{reply}\"");
        }
        try
        {
            return JsonNode.Parse(reply[start..])?["result"];
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"{command} returned invalid JSON: {reply}", e);
        }
    }

    /// <inheritdoc/>
    public override Task WaitAsync(TimeSpan time, CancellationToken cancellationToken = default) =>
        System.RunForAsync(time, cancellationToken);
}
