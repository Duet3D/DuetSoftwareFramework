using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetEmulation.Boards;
using DuetEmulation.Can;

namespace DuetEmulation;

/// <summary>The reply to a code</summary>
/// <param name="Text">Its text, without the severity prefix</param>
/// <param name="Severity">Whether it is a success, a warning or an error</param>
public sealed record CodeReply(string Text, MessageType Severity)
{
    /// <summary>
    /// A reply as text and severity: RepRapFirmware and DuetControlServer prefix each error and warning
    /// line with its severity
    /// </summary>
    public static CodeReply Parse(string reply)
    {
        const string errorPrefix = "Error: ";
        const string warningPrefix = "Warning: ";
        string[] lines = [.. reply.Split('\n').Select(l => l.TrimEnd('\r'))];
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
    public override string ToString() => Severity == MessageType.Success ? Text : $"{Severity}: {Text}";
}

/// <summary>The software that drives the boards</summary>
public enum Stack
{
    /// <summary>RepRapFirmware on the main board, standalone</summary>
    Rrf,

    /// <summary>DuetCANMaster on the main board, driven by DuetControlServer</summary>
    Dsf
}

/// <summary>A code that has been sent and may not have finished</summary>
/// <param name="Bench">The bench it was sent to</param>
/// <param name="Code">The code</param>
/// <param name="Reply">Its reply, which completes when it has finished</param>
public sealed record PendingCode(Bench Bench, string Code, Task<CodeReply> Reply)
{
    /// <summary>Let emulated time pass until the code has finished, and return its reply</summary>
    /// <exception cref="TimeoutException">It did not finish within <paramref name="timeout"/> of emulated time</exception>
    public async Task<CodeReply> WaitAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        await Bench.WaitUntilAsync(() => Task.FromResult(Reply.IsCompleted), timeout ?? Bench.DefaultCodeTimeout, $"\"{Code}\" to finish",
                                   cancellationToken);
        return await Reply;
    }
}

/// <summary>
/// An emulated machine under test: the boards in Renode and the stack that drives them, with what a
/// test needs to send codes and observe the result (replies, the object model, the CAN bus and the
/// boards' pins)
/// </summary>
public abstract class Bench : IAsyncDisposable
{
    /// <summary>How long a code may take, in emulated time, unless the caller says otherwise</summary>
    public static readonly TimeSpan DefaultCodeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How far emulated time advances between checks of a condition</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>The boards</summary>
    public EmulatedSystem System { get; }

    /// <summary>The frames on the CAN bus</summary>
    public CanTrace Can => System.Can;

    /// <summary>The boards' pins: what drives their inputs and what their outputs have done</summary>
    public BoardIo Io { get; }

    /// <summary>
    /// The line config.g has after the stack's own first line: a macro that holds config.g there until
    /// <see cref="RunConfigAsync"/>, so that the unconfigured machine can be read and its inputs set first
    /// </summary>
    public const string HoldLine = "M98 P\"0:/emulation/hold.g\"";

    // The file whose deletion lets config.g carry on, and the one the hold deletes once config.g is waiting
    private const string HeldFile = "0:/emulation/held";
    private const string WaitingFile = "0:/emulation/waiting";

    /// <summary>The files the hold needs on the SD card, by path on the card</summary>
    private static readonly IReadOnlyDictionary<string, string> HoldFiles = new Dictionary<string, string>
    {
        ["emulation/hold.g"] = $"M30 \"{WaitingFile}\"\nwhile fileexists(\"{HeldFile}\")\n  G4 P10\n",
        ["emulation/held"] = "held\n",
        ["emulation/waiting"] = "waiting\n"
    };

    /// <summary>config.g as the SD card has it: the stack's first lines, the hold, then the scenario's config.g</summary>
    protected static string ConfigFile(string stackPreamble, string config) => $"{stackPreamble}{HoldLine}\n{config}";

    /// <summary>Whether config.g is held at its start, waiting for <see cref="RunConfigAsync"/></summary>
    public bool ConfigHeld { get; private set; }

    /// <summary>Create a bench over a system whose machines exist but have not run yet</summary>
    protected Bench(EmulatedSystem system)
    {
        System = system;
        Io = new BoardIo(system);
    }

    /// <summary>
    /// Create the boards with <paramref name="config"/> as config.g (after whatever line the stack needs
    /// first, and the hold), start the stack, boot them, and run config.g unless <paramref name="holdConfig"/>
    /// </summary>
    /// <param name="stack">The software that drives the boards</param>
    /// <param name="expansionBoards">The expansion boards, by CAN address</param>
    /// <param name="config">config.g, less the stack's own first line</param>
    /// <param name="workDirectory">Where the run's files go</param>
    /// <param name="files">Further files for the SD card, by path on the card</param>
    /// <param name="holdConfig">
    /// Whether to leave config.g held at its start, for the caller to read the unconfigured machine, set its
    /// inputs and then call <see cref="RunConfigAsync"/>
    /// </param>
    /// <param name="cancellationToken">Cancels starting</param>
    public static async Task<Bench> StartAsync(Stack stack, IReadOnlyDictionary<int, BoardType> expansionBoards, string config, string workDirectory,
                                               IReadOnlyDictionary<string, string>? files = null, bool holdConfig = false,
                                               CancellationToken cancellationToken = default)
    {
        Bench bench = stack switch
        {
            Stack.Rrf => await Rrf.RrfBench.CreateAsync(expansionBoards, config, workDirectory, files, cancellationToken),
            Stack.Dsf => await Dsf.DsfBench.CreateAsync(expansionBoards, config, workDirectory, files, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(stack))
        };
        if (!holdConfig)
        {
            try
            {
                await bench.RunConfigAsync(cancellationToken: cancellationToken);
            }
            catch
            {
                await bench.DisposeAsync();
                throw;
            }
        }
        return bench;
    }

    /// <summary>The SD card's files for a bench: the scenario's, then the hold's</summary>
    protected static Dictionary<string, string> SdCardFiles(IReadOnlyDictionary<string, string>? files)
    {
        Dictionary<string, string> result = new(files ?? new Dictionary<string, string>());
        foreach ((string path, string contents) in HoldFiles)
        {
            result[path] = contents;
        }
        return result;
    }

    /// <summary>Let config.g carry on from its hold, and wait until the stack has run it and the startup files after it</summary>
    /// <exception cref="InvalidOperationException">config.g is not held</exception>
    /// <exception cref="TimeoutException">config.g did not finish within <paramref name="timeout"/> of emulated time (60s by default)</exception>
    public async Task RunConfigAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!ConfigHeld)
        {
            throw new InvalidOperationException("config.g is not held");
        }
        CodeReply released = await SendCodeAsync($"M30 \"{HeldFile}\"", cancellationToken: cancellationToken);
        if (released.Severity == MessageType.Error)
        {
            throw new InvalidOperationException($"Could not let config.g carry on: {released.Text}");
        }
        ConfigHeld = false;
        await WaitUntilAsync(async () => !await IsStartingAsync(cancellationToken), timeout ?? DefaultConfigTimeout, "config.g to finish", cancellationToken);
    }

    /// <summary>How long config.g may take, in emulated time, unless the caller says otherwise</summary>
    public static readonly TimeSpan DefaultConfigTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Whether the stack is still starting: running config.g and the startup files after it</summary>
    protected virtual async Task<bool> IsStartingAsync(CancellationToken cancellationToken) =>
        (await GetObjectModelAsync("state.status", cancellationToken))?.GetValue<string>() == "starting";

    /// <summary>Send one code without waiting for it to finish</summary>
    public abstract Task<PendingCode> StartCodeAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Send one code and return its reply once it has finished</summary>
    /// <exception cref="TimeoutException">It did not finish within <paramref name="timeout"/> of emulated time</exception>
    public async Task<CodeReply> SendCodeAsync(string code, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        await (await StartCodeAsync(code, cancellationToken)).WaitAsync(timeout, cancellationToken);

    /// <summary>The object model at <paramref name="key"/>, or the whole of it for an empty key</summary>
    public abstract Task<JsonNode?> GetObjectModelAsync(string key = "", CancellationToken cancellationToken = default);

    /// <summary>
    /// The whole object model as a capture compares it: every key in full. A stack may answer this
    /// from what it read last time, refreshing only the keys that have changed since
    /// </summary>
    public virtual Task<JsonNode?> GetObjectModelSnapshotAsync(CancellationToken cancellationToken = default) =>
        GetObjectModelAsync(string.Empty, cancellationToken);

    /// <summary>Let <paramref name="time"/> of emulated time pass</summary>
    public abstract Task WaitAsync(TimeSpan time, CancellationToken cancellationToken = default);

    /// <summary>
    /// Let the stack finish what it is doing without letting emulated time pass, so that what a test
    /// reads next is the same on every run. The emulated machines stand still between calls, so only
    /// a stack that runs outside them has anything to finish.
    /// </summary>
    public virtual Task QuiesceAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Wait until <paramref name="condition"/> holds, for up to <paramref name="timeout"/> of emulated time,
    /// and return the emulated time that passed
    /// </summary>
    /// <exception cref="TimeoutException">It did not hold in time</exception>
    public async Task<TimeSpan> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string what,
                                               CancellationToken cancellationToken = default)
    {
        TimeSpan start = System.Time;
        await QuiesceAsync(cancellationToken);
        while (!await condition())
        {
            if (System.Time - start >= timeout)
            {
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0.###}s of emulated time waiting for {what}");
            }
            await WaitAsync(DefaultPollInterval, cancellationToken);
        }
        return System.Time - start;
    }

    /// <summary>Wait until a CAN frame matching the query has been sent since the trace's mark, and return the first</summary>
    public async Task<CanFrame> WaitForFrameAsync(Func<CanTrace, CanFrame?> query, TimeSpan? timeout = null, string what = "a CAN frame",
                                                  CancellationToken cancellationToken = default)
    {
        CanFrame? found = null;
        await WaitUntilAsync(() => Task.FromResult((found = query(Can)) is not null), timeout ?? DefaultCodeTimeout, what, cancellationToken);
        return found!;
    }

    /// <summary>Wait for the complete standard reply to a request</summary>
    public async Task<StandardReply> WaitForReplyAsync(CanFrame request, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        StandardReply? reply = null;
        await WaitUntilAsync(() => Task.FromResult((reply = Can.ReplyTo(request)) is not null), timeout ?? DefaultCodeTimeout,
                             $"the reply to {request.MessageType} from board {request.Destination}", cancellationToken);
        return reply!;
    }

    /// <summary>
    /// Run until the system is ready to configure: the stack answers codes, config.g is held at its start,
    /// every expansion board is running in the object model, and their step clocks are synchronised to the
    /// main board's, so they will execute moves
    /// </summary>
    protected async Task BootAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(30);
        TimeSpan start = System.Time;
        await StartStackAsync(limit, cancellationToken);
        await WaitUntilAsync(async () => (await SendCodeAsync($"echo fileexists(\"{WaitingFile}\")", cancellationToken: cancellationToken)).Text == "false",
                             limit - (System.Time - start), "config.g to reach its hold", cancellationToken);
        ConfigHeld = true;
        EmulatedBoard[] expansion = [.. System.Boards.Where(b => b.Type.IsExpansion)];
        if (expansion.Length == 0)
        {
            return;
        }

        await WaitUntilAsync(async () =>
        {
            JsonArray boards = (await GetObjectModelAsync("boards", cancellationToken))?.AsArray() ?? [];
            return expansion.All(b => boards.Any(m => m?["canAddress"]?.GetValue<int>() == b.Address &&
                                                      m["state"]?.GetValue<string>() == "running"));
        }, limit - (System.Time - start), $"boards {string.Join(", ", expansion.Select(b => b.Address))} to be running", cancellationToken);

        await WaitUntilAsync(async () =>
        {
            foreach (EmulatedBoard board in expansion)
            {
                if (!await ExpansionBoardSyncedAsync(board, cancellationToken))
                {
                    return false;
                }
            }
            return true;
        }, limit - (System.Time - start), "the expansion boards to synchronise their clocks", cancellationToken);
    }

    /// <summary>Run until the stack answers codes, which it does from config.g's first line</summary>
    protected abstract Task StartStackAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Whether an expansion board's step clock is synchronised to the main board's, which it must be
    /// before it executes moves (Duet3Expansion's StepTimer::IsSynced, read from its memory)
    /// </summary>
    public async Task<bool> ExpansionBoardSyncedAsync(EmulatedBoard board, CancellationToken cancellationToken = default)
    {
        // StepTimer::syncCount, which StepTimer::IsSynced compares with StepTimer::MaxSyncCount
        const string syncCountSymbol = "_ZN9StepTimer9syncCountE";
        const uint maxSyncCount = 10;
        uint syncCount = await System.ReadDoubleWordAsync(board, board.Firmware.Symbols[syncCountSymbol], cancellationToken);
        return syncCount >= maxSyncCount;
    }

    /// <inheritdoc/>
    public virtual async ValueTask DisposeAsync()
    {
        await System.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
