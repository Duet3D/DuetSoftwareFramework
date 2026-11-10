using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Heat;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Codes;

/// <summary>
/// M108, ending temperature waits, asserted against RepRapFirmware's GCodes2.cpp case 108,
/// GCodes::CancelWaitForTemperatures and GCodeBuffer::IsWaitingForTemperatures, and against the regression suite's
/// m108-cancel-heating-wait reference
/// </summary>
[TestFixture]
public class CancelHeatingCodeTests : BenchFixture
{
    /// <summary>Long enough for a wait to have polled its heaters more than once</summary>
    private static readonly System.TimeSpan Settle = System.TimeSpan.FromMilliseconds(500);

    /// <summary>Tool 0 with heater 3, heater 4 as the bed and heater 5 as the chamber, all on board 1</summary>
    private const string HeaterConfig = """
        M308 S3 P"1.temp0" Y"thermistor" T100000 B4138
        M308 S4 P"1.temp1" Y"thermistor" T100000 B4138
        M308 S5 P"1.temp2" Y"thermistor" T100000 B4138
        M950 H3 C"1.out0" T3
        M950 H4 C"1.out1" T4
        M950 H5 C"1.out2" T5
        M563 P0 S"tool-zero" H3
        M140 H4
        M141 H5
        """;

    /// <summary>Start with every heater off at 25C, then run the setup code, which may switch heaters on</summary>
    private static async Task<(JobBench Bench, HeatManager Heat)> StartAsync(string setup = "")
    {
        JobBench bench = await JobControlBench.StartAsync(configExtra: HeaterConfig);
        foreach (int heater in new[] { 3, 4, 5 })
        {
            await ReportTemperatureAsync(bench, heater, HeaterMode.Off, 25.0f);
        }
        if (setup.Length > 0)
        {
            await bench.Host.ExecuteCodeAsync(setup);
        }
        bench.CanMaster.ClearCapture();
        return (bench, bench.Host.Services.GetRequiredService<HeatManager>());
    }

    private static async Task ReportTemperatureAsync(JobBench bench, int heater, HeaterMode mode, float temperature)
    {
        bench.CanMaster.InjectHeatersStatus(srcAddress: 1, heaterNumber: heater, mode, currentTemperature: temperature);
        await bench.CanMaster.WaitUntilAsync(() => bench.Host.Model.Heat.Heaters[heater]?.Current is float t && System.MathF.Abs(t - temperature) < 0.01f,
                                             what: $"heater {heater} reporting {temperature}C");
    }

    /// <summary>
    /// M108 ends a wait, which completes successfully with no reply, and leaves the heater on at its target. M108 is
    /// sent from the same channel as the wait, which it reaches because it is prioritised
    /// </summary>
    /// <remarks>RRF truth: GCodes2.cpp case 108, and the IsCancelWaitRequested checks of case 116/190/191 and GCodeState::m109WaitForTemperature</remarks>
    [TestCase("T0", "M109 S60", 3)]
    [TestCase("M140 S60", "M116", 4)]
    [TestCase("", "M190 S60", 4)]
    [TestCase("", "M191 S60", 5)]
    public async Task EndsWait(string setup, string code, int heaterNumber)
    {
        (JobBench bench, HeatManager heat) = await StartAsync(setup);
        await using JobBench _ = bench;

        Task<string> waiting = bench.Host.ExecuteCodeAsync(code);
        await bench.CanMaster.WaitUntilAsync(() => heat.IsWaitingForTemperatures, what: $"{code} waiting");
        bench.CanMaster.ClearCapture();

        Assert.That((await bench.Host.ExecuteCodeAsync("M108")).Trim(), Is.Empty, "M108 replies nothing");
        Assert.That((await waiting.WaitAsync(System.TimeSpan.FromSeconds(5))).Trim(), Is.Empty, $"{code} completes successfully");

        Heater heater = await bench.Host.ReadModelAsync(model => model.Heat.Heaters[heaterNumber]!);
        Assert.Multiple(() =>
        {
            Assert.That(heat.IsWaitingForTemperatures, Is.False, "nothing is waiting");
            Assert.That(bench.CanMaster.CanMessages<CanMessageSetHeaterTemperatureV1>(), Is.Empty, "M108 sends nothing");
            Assert.That(heater.State, Is.EqualTo(HeaterState.Active), "the heater stays on");
            Assert.That(heater.Active, Is.EqualTo(60.0f), "at its target");
        });
    }

    /// <summary>One M108 ends the waits of every channel</summary>
    /// <remarks>RRF truth: GCodes::CancelWaitForTemperatures(false) cancels every GCodeBuffer that is waiting</remarks>
    [Test]
    public async Task EndsWaitsOnEveryChannel()
    {
        (JobBench bench, HeatManager heat) = await StartAsync("M141 S60");
        await using JobBench _ = bench;

        Task<string> bedWait = bench.Host.ExecuteCodeAsync("M190 S60", CodeChannel.HTTP);
        Task<string> allWait = bench.Host.ExecuteCodeAsync("M116", CodeChannel.Daemon);
        await bench.CanMaster.WaitUntilAsync(() => heat.IsWaitingForTemperatures, what: "the waits starting");
        await Task.Delay(Settle);
        Assert.That(bedWait.IsCompleted || allWait.IsCompleted, Is.False, "both channels are waiting");

        Assert.That((await bench.Host.ExecuteCodeAsync("M108", CodeChannel.USB)).Trim(), Is.Empty, "M108 replies nothing");
        Assert.Multiple(async () =>
        {
            Assert.That((await bedWait.WaitAsync(System.TimeSpan.FromSeconds(5))).Trim(), Is.Empty, "M190 completes successfully");
            Assert.That((await allWait.WaitAsync(System.TimeSpan.FromSeconds(5))).Trim(), Is.Empty, "M116 completes successfully");
        });
    }

    /// <summary>M108 with nothing waiting does nothing, and does not end a wait that starts after it</summary>
    /// <remarks>
    /// RRF truth: CancelWaitForTemperatures only flags a GCodeBuffer for which IsWaitingForTemperatures is true.
    /// Regression: m108-cancel-heating-wait
    /// </remarks>
    [Test]
    public async Task NothingWaiting()
    {
        (JobBench bench, HeatManager heat) = await StartAsync();
        await using JobBench _ = bench;

        Assert.That((await bench.Host.ExecuteCodeAsync("M108")).Trim(), Is.Empty, "M108 replies nothing");
        Assert.That(bench.CanMaster.CanMessages<CanMessageSetHeaterTemperatureV1>(), Is.Empty, "M108 sends nothing");

        Task<string> waiting = bench.Host.ExecuteCodeAsync("M190 S60");
        await bench.CanMaster.WaitUntilAsync(() => heat.IsWaitingForTemperatures, what: "M190 waiting");
        await Task.Delay(Settle);
        Assert.That(waiting.IsCompleted, Is.False, "the earlier M108 does not end the wait");

        await ReportTemperatureAsync(bench, 4, HeaterMode.Stable, 60.0f);
        Assert.That((await waiting.WaitAsync(System.TimeSpan.FromSeconds(5))).Trim(), Is.Empty, "the heater reaching its target ends it");
    }
}
