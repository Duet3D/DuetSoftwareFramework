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

    // The last snapshot of each top-level key in full, and the sequence numbers it was read at
    private readonly Dictionary<string, JsonNode?> _snapshot = [];
    private readonly Dictionary<string, long> _seqs = [];

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

    private static async Task<CodeReply> ParseAsync(Task<string> reply) => CodeReply.Parse(await reply);

    /// <inheritdoc/>
    public override async Task<PendingCode> StartCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        Task<string> reply = await _console.SendAsync(code, cancellationToken);
        return new PendingCode(this, code, ParseAsync(reply));
    }

    /// <inheritdoc/>
    public override async Task<JsonNode?> GetObjectModelAsync(string key = "", CancellationToken cancellationToken = default)
    {
        // d99: every level; v: verbose (every field); n: include nulls
        return await QueryAsync(key, "d99vn", cancellationToken);
    }

    // M409 with a key and flags
    private async Task<JsonNode?> QueryAsync(string key, string flags, CancellationToken cancellationToken)
    {
        string command = $"M409 K\"{key}\" F\"{flags}\"";
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

    /// <summary>
    /// The whole object model, kept from the last snapshot and refreshed the way Duet Web Control
    /// refreshes it: a key is read again in full only when its sequence number has changed, and the
    /// live values, which change without one, are read on every snapshot
    /// </summary>
    public override async Task<JsonNode?> GetObjectModelSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (_snapshot.Count == 0)
        {
            JsonObject keys = (await QueryAsync("", "", cancellationToken))?.AsObject() ?? [];
            foreach (string key in keys.Select(k => k.Key))
            {
                _snapshot[key] = null;
            }
        }

        JsonObject seqs = (await QueryAsync("seqs", "", cancellationToken))?.AsObject() ?? [];
        foreach (string key in _snapshot.Keys.ToList())
        {
            long? seq = seqs[key]?.GetValue<long>();
            bool stale = _snapshot[key] is null || (seq is long now && (!_seqs.TryGetValue(key, out long then) || now != then));
            if (stale)
            {
                _snapshot[key] = await QueryAsync(key, "d99vn", cancellationToken);
                if (seq is long read)
                {
                    _seqs[key] = read;
                }
            }
        }

        JsonObject live = (await QueryAsync("", "d99fn", cancellationToken))?.AsObject() ?? [];
        JsonObject model = [];
        foreach ((string key, JsonNode? value) in _snapshot)
        {
            JsonNode? merged = value?.DeepClone();
            if (live[key] is JsonNode liveValue && key != "seqs")
            {
                merged = Overlay(merged, liveValue);
                _snapshot[key] = merged?.DeepClone();
            }
            model[key] = merged;
        }
        return model;
    }

    // Lay the live values over the full ones: objects member by member, arrays element by element
    private static JsonNode? Overlay(JsonNode? full, JsonNode live)
    {
        if (full is JsonObject fullObject && live is JsonObject liveObject)
        {
            foreach ((string key, JsonNode? value) in liveObject.ToList())
            {
                JsonNode? existing = fullObject[key];
                JsonNode? merged = value is null ? null : Overlay(existing, value);
                if (!ReferenceEquals(merged, existing))
                {
                    fullObject[key] = merged;
                }
            }
            return fullObject;
        }
        if (full is JsonArray fullArray && live is JsonArray liveArray)
        {
            for (int i = 0; i < liveArray.Count; i++)
            {
                JsonNode? element = liveArray[i];
                if (i < fullArray.Count)
                {
                    JsonNode? existing = fullArray[i];
                    JsonNode? merged = element is null ? null : Overlay(existing, element);
                    if (!ReferenceEquals(merged, existing))
                    {
                        fullArray[i] = merged;
                    }
                }
                else
                {
                    fullArray.Add(element?.DeepClone());
                }
            }
            return fullArray;
        }
        return live.DeepClone();
    }

    /// <inheritdoc/>
    public override async Task WaitAsync(TimeSpan time, CancellationToken cancellationToken = default)
    {
        await System.RunForAsync(time, cancellationToken);
        _console.Poll();
    }
}
