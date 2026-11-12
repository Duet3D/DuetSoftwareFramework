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
/// DuetControlServer runs in real time, so the emulation runs continuously rather than only while a
/// call waits: a pump lets emulated time pass a slice at a time for as long as the bench is up. What a
/// test sees is therefore not repeatable to the emulated microsecond as it is on the RepRapFirmware
/// bench, though waits are still measured in emulated time
/// </remarks>
public sealed class DsfBench : Bench
{
    /// <summary>The line config.g starts with: DuetControlServer drives CAN only once it is enabled</summary>
    public const string ConfigPreamble = "M953\n";

    // The global variable config.g ends by creating, which says it has run to the end
    private const string ConfigDoneVariable = "emulationConfigDone";

    // How much emulated time each step of the pump lets pass
    private static readonly TimeSpan PumpSlice = TimeSpan.FromMilliseconds(5);

    private readonly CancellationTokenSource _stop = new();
    private readonly string _socketDirectory;
    private Task _pump = Task.CompletedTask;
    private DcsHost? _dcs;

    /// <summary>DuetControlServer</summary>
    public DcsHost Dcs => _dcs ?? throw new InvalidOperationException("DuetControlServer has not started");

    private DsfBench(EmulatedSystem system, string socketDirectory) : base(system) => _socketDirectory = socketDirectory;

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
        EmulatedSystem system = await EmulatedSystem.CreateAsync(new SystemOptions
        {
            MainBoard = BoardTypes.Mb6hcCanMaster,
            ExpansionBoards = expansionBoards,
            WorkDirectory = workDirectory
        }, cancellationToken);

        // A Unix socket path is limited to 107 bytes, which a run directory can exceed, so the sockets
        // (the SBC link's and DuetControlServer's IPC socket) go in a short directory of their own
        string socketDirectory = Directory.CreateTempSubdirectory("duet-emulation-").FullName;
        string socketPath = Path.Combine(socketDirectory, "sbc.sock");
        DsfBench bench = new(system, socketDirectory);
        try
        {
            string bridge = system.Main.Type.SbcBridge ?? throw new InvalidOperationException($"{system.Main.Type.Name} has no SBC port");
            await system.ExecuteAsync(system.Main, $"{bridge} ListenForSbc {Emulator.MonitorSyntax.Quote(socketPath)}", cancellationToken);
            bench._pump = Task.Run(() => bench.PumpAsync(bench._stop.Token), CancellationToken.None);

            Dictionary<string, string> sdCard = new() { ["sys/config.g"] = $"{ConfigPreamble}{config}\nglobal {ConfigDoneVariable} = 1\n" };
            foreach ((string path, string contents) in files ?? new Dictionary<string, string>())
            {
                sdCard[path] = contents;
            }
            bench._dcs = await DcsHost.StartAsync(system.WorkDirectory, socketDirectory, socketPath, sdCard, cancellationToken);
            await bench.BootAsync(TimeSpan.FromSeconds(60), cancellationToken);
            return bench;
        }
        catch
        {
            await bench.DisposeAsync();
            throw;
        }
    }

    // Let emulated time pass for as long as the bench is up. Commands to Renode from tests and from
    // DuetControlServer's link run between the slices
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await System.RunForAsync(PumpSlice, cancellationToken);
        }
    }

    /// <inheritdoc/>
    protected override async Task StartStackAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await WaitUntilAsync(async () => await Dcs.ReadModelAsync(m => m.Global.ContainsKey(ConfigDoneVariable), cancellationToken),
                             timeout, "DuetControlServer to connect and run config.g", cancellationToken);
    }

    /// <inheritdoc/>
    public override Task<PendingCode> StartCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        ThrowIfPumpFailed();
        return Task.FromResult(new PendingCode(this, code, ExecuteAsync(code, cancellationToken)));
    }

    private async Task<CodeReply> ExecuteAsync(string code, CancellationToken cancellationToken) =>
        CodeReply.Parse(await Dcs.ExecuteCodeAsync(code, cancellationToken));

    /// <inheritdoc/>
    public override async Task<JsonNode?> GetObjectModelAsync(string key = "", CancellationToken cancellationToken = default)
    {
        byte[] json = await Dcs.ReadModelAsync(m => m.ToUtf8Json(), cancellationToken);
        return ObjectModelKey.Select(JsonNode.Parse(json), key);
    }

    /// <inheritdoc/>
    public override async Task WaitAsync(TimeSpan time, CancellationToken cancellationToken = default)
    {
        TimeSpan until = System.Time + time;
        while (System.Time < until)
        {
            ThrowIfPumpFailed();
            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
        }
    }

    private void ThrowIfPumpFailed()
    {
        if (_pump.IsFaulted)
        {
            throw new InvalidOperationException("The emulation stopped", _pump.Exception);
        }
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (_dcs is not null)
        {
            await _dcs.DisposeAsync();
        }
        await _stop.CancelAsync();
        try
        {
            await _pump;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Emulator.RenodeException)
        {
        }
        await base.DisposeAsync();
        Directory.Delete(_socketDirectory, recursive: true);
        _stop.Dispose();
    }
}
