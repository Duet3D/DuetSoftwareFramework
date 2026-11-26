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

    /// <summary>Create a bench over a system whose machines exist but have not run yet</summary>
    protected Bench(EmulatedSystem system)
    {
        System = system;
        Io = new BoardIo(system);
    }

    /// <summary>
    /// Create the boards with <paramref name="config"/> as config.g (after whatever line the stack needs
    /// first), start the stack, and boot them
    /// </summary>
    /// <param name="stack">The software that drives the boards</param>
    /// <param name="expansionBoards">The expansion boards, by CAN address</param>
    /// <param name="config">config.g, less the stack's own first line</param>
    /// <param name="workDirectory">Where the run's files go</param>
    /// <param name="files">Further files for the SD card, by path on the card</param>
    /// <param name="cancellationToken">Cancels starting</param>
    public static async Task<Bench> StartAsync(Stack stack, IReadOnlyDictionary<int, BoardType> expansionBoards, string config, string workDirectory,
                                               IReadOnlyDictionary<string, string>? files = null, CancellationToken cancellationToken = default) =>
        stack switch
        {
            Stack.Rrf => await Rrf.RrfBench.StartAsync(expansionBoards, config, workDirectory, files, cancellationToken),
            Stack.Dsf => await Dsf.DsfBench.StartAsync(expansionBoards, config, workDirectory, files, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(stack))
        };

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
    /// Run until the system is ready to use: the stack has started, every expansion board is running in
    /// the object model, and their step clocks are synchronised to the main board's, so they will
    /// execute moves
    /// </summary>
    public async Task BootAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(30);
        TimeSpan start = System.Time;
        await StartStackAsync(limit, cancellationToken);
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

    /// <summary>Run until the stack answers codes</summary>
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
