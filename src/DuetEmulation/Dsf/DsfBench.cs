using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DuetEmulation.Boards;

namespace DuetEmulation.Dsf;

/// <summary>
/// An MB6HC running DuetCANMaster, driven by DuetControlServer in-process over the emulated SBC SPI port,
/// with its expansion boards: codes go in as DuetControlServer commands and the object model is
/// DuetControlServer's own
/// </summary>
/// <remarks>
/// DuetControlServer and DuetRealtimeCore run in lockstep with the emulation (<see cref="SbcLockstep"/>),
/// on emulated time, so as on the RepRapFirmware bench emulated time passes only while a call waits,
/// and a test does the same things at the same emulated times on every run.
/// <para>
/// One DSF bench runs in a process at a time: DuetRealtimeCore's clock is process-wide
/// </para>
/// </remarks>
public sealed class DsfBench : Bench
{
    /// <summary>The line config.g starts with: DuetControlServer drives CAN only once it is enabled</summary>
    public const string ConfigPreamble = "M953\n";

    // The global variable config.g ends by creating, which says it has run to the end
    private const string ConfigDoneVariable = "emulationConfigDone";

    // Real time between moves of DuetControlServer's clock while it shuts down, when the emulation no longer runs
    private static readonly TimeSpan ShutdownTick = TimeSpan.FromMilliseconds(10);

    private static int s_running;

    private readonly string _socketDirectory;
    private readonly SbcLockstep _lockstep;
    private DcsHost? _dcs;

    /// <summary>DuetControlServer</summary>
    public DcsHost Dcs => _dcs ?? throw new InvalidOperationException("DuetControlServer has not started");

    private DsfBench(EmulatedSystem system, string socketDirectory, string sbcBridge) : base(system)
    {
        _socketDirectory = socketDirectory;
        _lockstep = new SbcLockstep(system, sbcBridge);
    }

    /// <summary>
    /// Create the boards, start the emulation and DuetControlServer with <paramref name="config"/> as
    /// config.g after the line that enables CAN, and boot them
    /// </summary>
    /// <param name="expansionBoards">The expansion boards, by CAN address</param>
    /// <param name="config">The rest of config.g</param>
    /// <param name="workDirectory">Where the run's files go</param>
    /// <param name="files">Further files for the SD card, by path on the card</param>
    /// <param name="cancellationToken">Cancels starting</param>
    public static async Task<DsfBench> StartAsync(IReadOnlyDictionary<int, BoardType> expansionBoards, string config, string workDirectory,
                                                  IReadOnlyDictionary<string, string>? files = null, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref s_running, 1, 0) != 0)
        {
            throw new InvalidOperationException("Another DSF bench is running in this process, and DuetRealtimeCore's step clock is process-wide");
        }
        EmulatedSystem system;
        try
        {
            system = await EmulatedSystem.CreateAsync(new SystemOptions
            {
                MainBoard = BoardTypes.Mb6hcCanMaster,
                ExpansionBoards = expansionBoards,
                WorkDirectory = workDirectory
            }, cancellationToken);
        }
        catch
        {
            Volatile.Write(ref s_running, 0);
            throw;
        }

        // A Unix socket path is limited to 107 bytes, which a run directory can exceed, so the sockets
        // (the SBC link's and DuetControlServer's IPC socket) go in a short directory of their own
        string socketDirectory;
        try
        {
            socketDirectory = Directory.CreateTempSubdirectory("duet-emulation-").FullName;
        }
        catch
        {
            await system.DisposeAsync();
            Volatile.Write(ref s_running, 0);
            throw;
        }
        string socketPath = Path.Combine(socketDirectory, "sbc.sock");
        DsfBench bench;
        try
        {
            string bridge = system.Main.Type.SbcBridge ?? throw new InvalidOperationException($"{system.Main.Type.Name} has no SBC port");
            bench = new(system, socketDirectory, bridge);
        }
        catch
        {
            Directory.Delete(socketDirectory, recursive: true);
            await system.DisposeAsync();
            Volatile.Write(ref s_running, 0);
            throw;
        }
        try
        {
            await system.ExecuteAsync(system.Main, $"{bench._lockstep.SbcBridge} ListenForSbc {Emulator.MonitorSyntax.Quote(socketPath)}", cancellationToken);

            Dictionary<string, string> sdCard = new() { ["sys/config.g"] = $"{ConfigPreamble}{config}\nglobal {ConfigDoneVariable} = 1\n" };
            foreach ((string path, string contents) in files ?? new Dictionary<string, string>())
            {
                sdCard[path] = contents;
            }

            // Starting DuetControlServer connects to the controller, which takes emulated time, so the
            // lockstep runs while it starts. It starts on the thread pool, where the lockstep sees it
            // work and sees it wait for the controller.
            Task<DcsHost> starting = Task.Run(() => DcsHost.StartAsync(system.WorkDirectory, socketDirectory, socketPath, sdCard,
                                                                       bench._lockstep.Clock, cancellationToken), cancellationToken);
            while (!starting.IsCompleted)
            {
                await bench._lockstep.RunForAsync(system.Quantum, cancellationToken);
            }
            bench._dcs = await starting;
            await bench.BootAsync(TimeSpan.FromSeconds(60), cancellationToken);
            return bench;
        }
        catch
        {
            await bench.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc/>
    protected override async Task StartStackAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await WaitUntilAsync(async () => await ReadModelAsync(m => m.Global.ContainsKey(ConfigDoneVariable), cancellationToken),
                             timeout, "DuetControlServer to connect and run config.g", cancellationToken);
    }

    /// <inheritdoc/>
    public override Task<PendingCode> StartCodeAsync(string code, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PendingCode(this, code, ExecuteAsync(code, cancellationToken)));

    private async Task<CodeReply> ExecuteAsync(string code, CancellationToken cancellationToken) =>
        CodeReply.Parse(await Dcs.ExecuteCodeAsync(code, cancellationToken));

    /// <inheritdoc/>
    public override async Task<JsonNode?> GetObjectModelAsync(string key = "", CancellationToken cancellationToken = default)
    {
        await QuiesceAsync(cancellationToken);
        byte[] json = await ReadModelAsync(m => m.ToUtf8Json(), cancellationToken);
        return ObjectModelKey.Select(JsonNode.Parse(json), key);
    }

    /// <summary>
    /// Read the object model under its lock. A code may hold the lock while it waits for the boards,
    /// which needs emulated time, so the emulation runs until the lock is free
    /// </summary>
    private async Task<T> ReadModelAsync<T>(Func<DuetControlServer.Model.ObjectModel, T> read, CancellationToken cancellationToken)
    {
        Task<T> reading = Dcs.ReadModelAsync(read, cancellationToken);
        while (!reading.IsCompleted)
        {
            await _lockstep.CatchUpAsync(cancellationToken);
            if (!reading.IsCompleted)
            {
                await _lockstep.RunForAsync(System.Quantum, cancellationToken);
            }
        }
        return await reading;
    }

    /// <inheritdoc/>
    public override Task WaitAsync(TimeSpan time, CancellationToken cancellationToken = default) => _lockstep.RunForAsync(time, cancellationToken);

    /// <inheritdoc/>
    public override Task QuiesceAsync(CancellationToken cancellationToken = default) => _lockstep.CatchUpAsync(cancellationToken);

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        try
        {
            if (_dcs is not null)
            {
                // Shutting down is not part of what a test observes, so DuetRealtimeCore goes back to
                // real time and DuetControlServer's clock is moved on in real time while it stops
                SbcLockstep.Release();
                Task stopping = _dcs.DisposeAsync().AsTask();
                while (!stopping.IsCompleted)
                {
                    _lockstep.Clock.AdvanceTo(_lockstep.Clock.Now + ShutdownTick);
                    while (_lockstep.Clock.FireNextDue())
                    {
                    }
                    await Task.WhenAny(stopping, Task.Delay(ShutdownTick));
                }
                await stopping;
            }
        }
        finally
        {
            SbcLockstep.Release();
            try
            {
                await base.DisposeAsync();
            }
            finally
            {
                Directory.Delete(_socketDirectory, recursive: true);
                Volatile.Write(ref s_running, 0);
            }
        }
    }
}
