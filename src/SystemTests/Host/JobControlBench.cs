using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SystemTests.Host;

/// <summary>
/// The shared pieces of the job control scenarios: one machine configuration, one instrumented set
/// of job system macros, and the helpers the assertions repeat. Each scenario still owns its job
/// file and the order it drives the lifecycle in
/// </summary>
internal static class JobControlBench
{
    /// <summary>A unique socket path for one test's fake controller</summary>
    public static string SocketPath() => Path.Combine(Path.GetTempPath(), $"dsf-fake-{Guid.NewGuid():N}.sock");

    /// <summary>
    /// X and Y axes plus one extruder on board 1 (board 0 runs DuetCANMaster and has no drivers),
    /// free to move without homing and with cold extrusion allowed, as the test jobs extrude with
    /// no heater configured. M953 comes first: with the bus disabled the configuration's CAN
    /// messages would be answered with BusError, as the real controller answers them. The closing
    /// G92 also marks X and Y homed, which the pause macros require
    /// </summary>
    /// TODO test with segmentation enabled
    public const string XyeConfig = """
        M953
        M569 P1.0 S1
        M569 P1.1 S1
        M569 P1.2 S1
        M584 X1.0 Y1.1 E1.2
        M92 X80 Y80 E420
        M906 X800 Y800 E800
        M201 X500 Y500 E250
        M203 X6000 Y6000 E3600
        M566 X900 Y900 E120
        M208 X0:200 Y0:200
        M302 P1
        M564 H0 S0
        G92 X0 Y0
        """;

    /// <summary>
    /// Globals the instrumented macros count their runs in, created by config.g so a scenario can
    /// read them before any macro ran
    /// </summary>
    private const string MarkerGlobals = """
        global startRan = 0
        global stopRan = 0
        global pauseRan = 0
        global resumeRan = 0
        global cancelRan = 0
        global filChangeRan = 0
        global macroRuns = 0
        """;

    /// <summary>
    /// Write config.g and the instrumented job system macros, each counting its runs in a global
    /// for the assertions. pause.g parks at X0 Y0 so a scenario can tell the restore point (taken
    /// before the park) from where pause.g leaves the machine; the other macros only mark that
    /// they ran
    /// </summary>
    /// <param name="sd">The virtual SD card to populate</param>
    /// <param name="configExtra">Extra configuration lines appended before the done marker</param>
    public static void WriteSystemFiles(VirtualSd sd, string configExtra = "")
    {
        sd.WriteSys("config.g", XyeConfig + "\n" + MarkerGlobals + "\n" + configExtra + DcsTestHost.ConfigDoneMarker);
        sd.WriteSys("start.g", "set global.startRan = global.startRan + 1\n");
        sd.WriteSys("stop.g", "set global.stopRan = global.stopRan + 1\n");
        sd.WriteSys("pause.g", "set global.pauseRan = global.pauseRan + 1\nG90\nG1 X0 Y0 F6000\n");
        sd.WriteSys("resume.g", "set global.resumeRan = global.resumeRan + 1\n");
        sd.WriteSys("cancel.g", "set global.cancelRan = global.cancelRan + 1\n");
        sd.WriteSys("filament-change.g", "set global.filChangeRan = global.filChangeRan + 1\n");
    }

    /// <summary>
    /// Start a complete job control bench: the fake controller answering CAN requests like healthy
    /// boards, and a host booted from the shared configuration and instrumented macros
    /// </summary>
    /// <param name="configExtra">Extra configuration lines, e.g. per-scenario globals</param>
    /// <param name="prepareSd">Populates the rest of the virtual SD card, typically the job file</param>
    public static async Task<JobBench> StartAsync(string configExtra = "", Action<VirtualSd>? prepareSd = null)
    {
        ScriptedCanMaster canMaster = new(SocketPath());
        canMaster.AckCanRequestsWithStandardReplies();
        DcsTestHost host;
        try
        {
            host = await DcsTestHost.StartAsync(canMaster, sd =>
            {
                WriteSystemFiles(sd, configExtra);
                prepareSd?.Invoke(sd);
            });
        }
        catch
        {
            canMaster.Dispose();
            throw;
        }
        await host.WaitForConfigDoneAsync();
        return new JobBench(canMaster, host);
    }

    /// <summary>
    /// A block of position-neutral zigzag moves. A job completes as soon as its last code is
    /// queued, so a scenario that pauses from the console keeps the job alive by carrying more
    /// moves than the ring holds (40 by default); these fillers are that padding. Each pair nets
    /// to nothing, so they change neither the final position nor any step total
    /// </summary>
    /// <param name="pairs">Number of out-and-back pairs; 30 pairs comfortably exceed the ring</param>
    /// <param name="feed">Feed rate in mm/min, trading how long the padding takes to execute</param>
    public static string FillerMoves(int pairs = 30, int feed = 6000)
    {
        System.Text.StringBuilder moves = new("G91\n");
        for (int i = 0; i < pairs; i++)
        {
            moves.Append($"G1 Y0.5 F{feed}\nG1 Y-0.5 F{feed}\n");
        }
        return moves.Append("G90\n").ToString();
    }

    /// <summary>Read an object model expression through the G-code interpreter, as its echoed text</summary>
    public static async Task<string> EvaluateRawAsync(this DcsTestHost host, string expression)
        => (await host.ExecuteCodeAsync($"echo {expression}")).Trim();

    /// <summary>Read a numeric object model expression through the G-code interpreter</summary>
    public static async Task<double> EvaluateAsync(this DcsTestHost host, string expression)
        => double.Parse(await host.EvaluateRawAsync(expression), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Read one of the macro run counters</summary>
    public static async Task<int> GlobalAsync(this DcsTestHost host, string name)
        => (int)await host.EvaluateAsync($"global.{name}");

    /// <summary>The coordinates of a restore point, read through the interpreter</summary>
    public static async Task<(double X, double Y)> RestorePointAsync(this DcsTestHost host, int slot)
        => (await host.EvaluateAsync($"state.restorePoints[{slot}].coords[0]"),
            await host.EvaluateAsync($"state.restorePoints[{slot}].coords[1]"));

    /// <summary>
    /// Wait until the machine is paused with the pause restore point at the given coordinates.
    /// This is the wait for the second of two pauses in one job: a resume's read-ahead can reach
    /// the next in-file pause so quickly that no intermediate status is ever observable, so the
    /// handover is only visible in the restore point moving on
    /// </summary>
    public static async Task WaitForPauseAtAsync(this DcsTestHost host, double x, double y, int timeoutMs = 20_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        (double X, double Y) point;
        do
        {
            point = await host.RestorePointAsync(1);
            if (Math.Abs(point.X - x) < 1e-3 && Math.Abs(point.Y - y) < 1e-3)
            {
                await host.WaitForStatusAsync(DuetAPI.ObjectModel.MachineStatus.Paused, timeoutMs);
                return;
            }
            await Task.Delay(25);
        }
        while (DateTime.UtcNow < deadline);
        throw new TimeoutException($"The pause restore point stayed at {point}, expected ({x}, {y})");
    }

    /// <summary>Every scheduled move's total distance so far, in order</summary>
    public static float[] MoveDistances(this ScriptedCanMaster canMaster)
        => canMaster.ScheduledMoves().Select(move => move.Header.TotalDistance).ToArray();

    /// <summary>
    /// The net steps scheduled per driver of board 1 so far. Steps are what the expansion board
    /// would execute, so their sum is the automated stand-in for where the physical head (or how
    /// much extruded filament) ends up
    /// </summary>
    public static int ScheduledSteps(this ScriptedCanMaster canMaster, byte driver)
        => canMaster.ScheduledMoves().Steps(driver);
}

/// <summary>One running job control bench: the fake controller and the host started against it</summary>
internal sealed class JobBench(ScriptedCanMaster canMaster, DcsTestHost host) : IAsyncDisposable
{
    /// <summary>The fake controller</summary>
    public ScriptedCanMaster CanMaster { get; } = canMaster;

    /// <summary>The hosted DuetControlServer</summary>
    public DcsTestHost Host { get; } = host;

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        CanMaster.Dispose();
    }
}
