using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetEmulation.Boards;
using DuetEmulation.Can;
using DuetEmulation.Emulator;
using DuetEmulation.Storage;
using DuetEmulation.Traces;

namespace DuetEmulation;

/// <summary>
/// A main board and any number of expansion boards, each its own machine in one Renode instance, on one
/// emulated CAN bus, each running the firmware image its build ships
/// </summary>
/// <remarks>
/// Emulated time passes only in <see cref="RunForAsync"/>, and the machines take turns a quantum at a
/// time, so the same sequence of calls does the same thing at the same emulated times on every run
/// </remarks>
public sealed class EmulatedSystem : IAsyncDisposable
{
    /// <summary>The name of the CAN hub the boards' controllers connect to</summary>
    private const string CanHub = "canBus";

    private readonly List<EmulatedBoard> _boards;
    private readonly SemaphoreSlim _timeLock = new(1, 1);
    private long _elapsedTicks;

    /// <summary>The Renode instance the machines run in</summary>
    public RenodeInstance Renode { get; }

    /// <summary>Every board, the main board first</summary>
    public IReadOnlyList<EmulatedBoard> Boards => _boards;

    /// <summary>The main board</summary>
    public EmulatedBoard Main => _boards[0];

    /// <summary>The frames on the CAN bus</summary>
    public CanTrace Can { get; }

    /// <summary>What the boards' output pins have driven</summary>
    public IoTrace Io { get; }

    /// <summary>Where the run's logs, traces and images are</summary>
    public string WorkDirectory { get; }

    /// <summary>The emulated time that has passed since the machines were created</summary>
    public TimeSpan Time => TimeSpan.FromTicks(Interlocked.Read(ref _elapsedTicks));

    private EmulatedSystem(RenodeInstance renode, List<EmulatedBoard> boards, string workDirectory)
    {
        Renode = renode;
        _boards = boards;
        WorkDirectory = workDirectory;
        Can = new CanTrace(Path.Combine(workDirectory, "can.jsonl"));
        Io = new IoTrace(Path.Combine(workDirectory, "io.jsonl"), boards);
    }

    /// <summary>Create the machines and load their firmware, without running them</summary>
    /// <exception cref="FileNotFoundException">A firmware image has not been built, or Renode is not installed</exception>
    public static async Task<EmulatedSystem> CreateAsync(SystemOptions options, CancellationToken cancellationToken = default)
    {
        List<EmulatedBoard> boards = [Describe(options.MainBoard, 0, EmulatedBoard.MainMachine, options.Debug)];
        if (options.MainBoard.IsExpansion)
        {
            throw new ArgumentException($"{options.MainBoard.Name} is not a main board", nameof(options));
        }
        foreach ((int address, BoardType type) in options.ExpansionBoards.OrderBy(b => b.Key))
        {
            if (!type.IsExpansion)
            {
                throw new ArgumentException($"{type.Name} is not an expansion board", nameof(options));
            }
            if (address < 1 || address > type.MaxAddress)
            {
                throw new ArgumentException($"A {type.Name}'s switches cannot set address {address}", nameof(options));
            }
            boards.Add(Describe(type, address, EmulatedBoard.ExpansionMachine(address), options.Debug));
        }

        string workDirectory = Path.GetFullPath(options.WorkDirectory);
        if (Directory.Exists(workDirectory))
        {
            Directory.Delete(workDirectory, recursive: true);
        }
        Directory.CreateDirectory(workDirectory);

        RenodeInstance renode = await RenodeInstance.StartAsync(Path.Combine(workDirectory, "renode.log"), cancellationToken);
        EmulatedSystem system = new(renode, boards, workDirectory);
        try
        {
            await system.BuildAsync(options, cancellationToken);
            return system;
        }
        catch
        {
            await system.DisposeAsync();
            throw;
        }
    }

    private static EmulatedBoard Describe(BoardType type, int address, string machine, bool debug)
    {
        string suffix = debug ? "-debug" : string.Empty;
        FirmwareImage firmware = FirmwareImage.Find(type.FirmwarePreset + suffix);
        FirmwareImage? bootloader = type.BootloaderPreset is null ? null : FirmwareImage.Find(type.BootloaderPreset + suffix);
        return new EmulatedBoard(machine, type, address, firmware, bootloader);
    }

    private async Task BuildAsync(SystemOptions options, CancellationToken cancellationToken)
    {
        await Renode.ExecuteAsync($"logFile {MonitorSyntax.PathArgument(Path.Combine(WorkDirectory, "emulation.log"))} true", cancellationToken: cancellationToken);
        string models = PeripheralModels.Combine(_boards.SelectMany(b => b.Type.Models));
        await Renode.ExecuteAsync($"include {MonitorSyntax.PathArgument(models)}", cancellationToken: cancellationToken);

        await Renode.ExecuteAsync($"emulation LogIoToFile {MonitorSyntax.Quote(Io.Path)}", cancellationToken: cancellationToken);
        await Renode.ExecuteAsync($"emulation CreateCANHub {MonitorSyntax.Quote(CanHub)}", cancellationToken: cancellationToken);
        await Renode.ExecuteAsync($"{CanHub} LogFramesToFile {MonitorSyntax.Quote(Can.Path)}", cancellationToken: cancellationToken);
        await Renode.ExecuteAsync($"emulation SetGlobalQuantum {MonitorSyntax.Seconds(options.Quantum)}", cancellationToken: cancellationToken);
        // Taking turns makes runs repeatable; in parallel, thread scheduling makes them differ
        await Renode.ExecuteAsync("emulation SetGlobalSerialExecution true", cancellationToken: cancellationToken);

        foreach (EmulatedBoard board in _boards)
        {
            await CreateMachineAsync(board, options, cancellationToken);
        }

        // Warnings include every access to a register that nothing models, which is what to model next
        await Renode.ExecuteAsync("logLevel 2", cancellationToken: cancellationToken);
        foreach (string entry in options.DebugLog)
        {
            string[] parts = entry.Split('/', 2);
            if (parts.Length != 2)
            {
                throw new ArgumentException($"\"{entry}\" is not machine/peripheral, such as main/spi1", nameof(options));
            }
            await Renode.ExecuteAsync($"logLevel 0 {parts[1]}", parts[0], cancellationToken);
        }
    }

    private async Task CreateMachineAsync(EmulatedBoard board, SystemOptions options, CancellationToken cancellationToken)
    {
        await Renode.ExecuteAsync($"mach create {MonitorSyntax.Quote(board.Machine)}", cancellationToken: cancellationToken);
        foreach ((string variable, string svd) in board.Type.Svds)
        {
            if (!File.Exists(svd))
            {
                throw new FileNotFoundException($"SVD file {svd} is missing (see .devcontainer/install-renode.sh)", svd);
            }
            await Renode.ExecuteAsync($"${variable}={MonitorSyntax.PathArgument(svd)}", cancellationToken: cancellationToken);
        }
        string platform = Path.Combine(RepositoryPaths.Renode, "Platforms", board.Type.Platform);
        await ExecuteAsync(board, $"machine LoadPlatformDescription {MonitorSyntax.PathArgument(platform)}", cancellationToken);
        foreach (string command in PinWiring.Commands(board.Type))
        {
            await ExecuteAsync(board, command, cancellationToken);
        }

        for (int bit = 0; bit < board.Type.AddressSwitches.Count; bit++)
        {
            // A switch that is on holds its pin low; the others float up to their pull-ups
            if ((board.Address & (1 << bit)) != 0)
            {
                (string gpio, int number) = board.Type.Gpio(board.Type.AddressSwitches[bit]);
                await ExecuteAsync(board, $"{gpio} Drive {number} false", cancellationToken);
            }
        }
        for (int word = 0; word < board.Type.SerialNumberWords.Count; word++)
        {
            // Each board gets its own serial number, derived from its address so that runs repeat
            uint serial = 0xD0E70000u | (uint)board.Address << 8 | (uint)word;
            await ExecuteAsync(board, $"sysbus WriteDoubleWord {MonitorSyntax.Hex(board.Type.SerialNumberWords[word])} {MonitorSyntax.Hex(serial)}", cancellationToken);
        }

        // Renode runs the machine's reset macro after every reset, so these also restore the firmware
        // and the boot vectors when the firmware resets itself (M999, a firmware update)
        List<string> reset = [];
        ulong start = board.Type.ApplicationAddress;
        if (board.Bootloader is not null)
        {
            reset.Add($"sysbus LoadBinary {MonitorSyntax.PathArgument(board.Bootloader.Binary)} 0x0");
            start = 0;
        }
        reset.Add($"sysbus LoadBinary {MonitorSyntax.PathArgument(board.Firmware.Binary)} {MonitorSyntax.Hex(board.Type.ApplicationAddress)}");
        reset.Add($"cpu VectorTableOffset {MonitorSyntax.Hex(start)}");
        reset.Add($"cpu PC `sysbus ReadDoubleWord {MonitorSyntax.Hex(start + 4)}`");
        reset.Add($"cpu SP `sysbus ReadDoubleWord {MonitorSyntax.Hex(start)}`");
        await ExecuteAsync(board, $"macro reset {MonitorSyntax.Quote(string.Join("; ", reset))}", cancellationToken);
        foreach (FirmwareImage image in new[] { board.Bootloader, board.Firmware }.OfType<FirmwareImage>())
        {
            await ExecuteAsync(board, $"sysbus LoadSymbolsFrom {MonitorSyntax.PathArgument(image.Elf)}", cancellationToken);
        }
        await ExecuteAsync(board, "runMacro $reset", cancellationToken);
        await ExecuteAsync(board, $"connector Connect sysbus.{board.Type.CanController} {CanHub}", cancellationToken);

        if (board.Type.SdCardController is string sdCard)
        {
            string image = Path.Combine(WorkDirectory, $"{board.Machine}-sd.img");
            SdCardImage.Create(image, options.SdCardFiles);
            await ExecuteAsync(board, $"machine SdCardFromFile {MonitorSyntax.PathArgument(image)} {sdCard} {MonitorSyntax.Hex(SdCardImage.Size)} true \"sd\"", cancellationToken);
        }
    }

    /// <summary>The board at a CAN address</summary>
    /// <exception cref="KeyNotFoundException">No board has that address</exception>
    public EmulatedBoard Board(int address) =>
        _boards.FirstOrDefault(b => b.Address == address) ?? throw new KeyNotFoundException($"No board has CAN address {address}");

    /// <summary>Run a monitor command on a board's machine</summary>
    public Task<string> ExecuteAsync(EmulatedBoard board, string command, CancellationToken cancellationToken = default) =>
        Renode.ExecuteAsync(command, board.Machine, cancellationToken);

    /// <summary>Read a 32-bit word of a board's memory</summary>
    public async Task<uint> ReadDoubleWordAsync(EmulatedBoard board, uint address, CancellationToken cancellationToken = default) =>
        (uint)await Renode.ExecuteForNumberAsync($"sysbus ReadDoubleWord {MonitorSyntax.Hex(address)}", board.Machine, cancellationToken);

    /// <summary>Let <paramref name="time"/> of emulated time pass, on every board</summary>
    public async Task RunForAsync(TimeSpan time, CancellationToken cancellationToken = default)
    {
        await _timeLock.WaitAsync(cancellationToken);
        try
        {
            await Renode.ExecuteAsync($"emulation RunFor {MonitorSyntax.Seconds(time)}", cancellationToken: cancellationToken);
            Interlocked.Add(ref _elapsedTicks, time.Ticks);
        }
        finally
        {
            _timeLock.Release();
        }
    }

    /// <summary>
    /// Run until <paramref name="condition"/> holds, checking it every <paramref name="step"/> of
    /// emulated time, and return the emulated time that passed
    /// </summary>
    /// <exception cref="TimeoutException"><paramref name="timeout"/> of emulated time passed first</exception>
    public async Task<TimeSpan> RunUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, TimeSpan step, string what,
                                              CancellationToken cancellationToken = default)
    {
        TimeSpan start = Time;
        while (!await condition())
        {
            if (Time - start >= timeout)
            {
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0.###}s of emulated time waiting for {what}");
            }
            await RunForAsync(step, cancellationToken);
        }
        return Time - start;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await Renode.DisposeAsync();
}
