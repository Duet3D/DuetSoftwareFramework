using System;
using System.Threading.Tasks;
using DuetControlServer.Heat;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Heat;

/// <summary>
/// M116, waiting for temperatures, asserted against RepRapFirmware's GCodes2.cpp case 116,
/// GCodes::ToolHeatersAtSetTemperatures, Heat::SlowHeatersAtSetTemperatures and Heat::HeaterAtSetTemperature, and against
/// the regression suite's m116-wait-for-all-temperatures reference
/// </summary>
[TestFixture]
[GCode("M116")]
public class M116Tests : BenchFixture
{
    /// <summary>
    /// Tool 0 with heater 3, tool 1 with heater 4, heater 5 as bed slot 0, heater 6 as chamber slot 0 and heater 7 as
    /// chamber slot 1, all on board 1
    /// </summary>
    private static readonly string HeaterConfig = $"""
        M308 S3 P"{Expansion.Temp(Machines.DriverBoard, 0)}" Y"thermistor" T100000 B4138
        M308 S4 P"{Expansion.Temp(Machines.DriverBoard, 1)}" Y"thermistor" T100000 B4138
        M308 S5 P"{Expansion.Temp(Machines.DriverBoard, 2)}" Y"thermistor" T100000 B4138
        M308 S6 P"{Expansion.Temp(Machines.DriverBoard, 3)}" Y"thermistor" T100000 B4138
        M308 S7 P"{Expansion.Temp(Machines.DriverBoard, 4)}" Y"thermistor" T100000 B4138
        M950 H3 C"{Expansion.Out(Machines.DriverBoard, 0)}" T3
        M950 H4 C"{Expansion.Out(Machines.DriverBoard, 1)}" T4
        M950 H5 C"{Expansion.Out(Machines.DriverBoard, 2)}" T5
        M950 H6 C"{Expansion.Out(Machines.DriverBoard, 3)}" T6
        M950 H7 C"{Expansion.Out(Machines.DriverBoard, 4)}" T7
        M563 P0 S"tool-zero" H3
        M563 P1 S"tool-one" H4
        M140 H5
        M141 H6
        M141 P1 H7
        """;

    /// <summary>Long enough for a wait to have polled its heaters more than once</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);

    /// <summary>Start with every heater off at 25C, then run the setup code, which switches heaters on</summary>
    private static async Task<(Bench Bench, HeatManager Heat)> StartAsync(string setup)
    {
        Bench bench = await Bench.StartAsync(HeaterConfig);
        for (int heater = 3; heater <= 7; heater++)
        {
            await bench.ReportHeaterAsync(heater, HeaterMode.Off, 25.0f);
        }
        foreach (string code in setup.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.That((await bench.Host.ExecuteCodeAsync(code)).Trim(), Is.Empty, $"setup {code}");
        }
        return (bench, bench.Host.Services.GetRequiredService<HeatManager>());
    }

    /// <summary>Run the code and assert that it is still waiting after a while, then end the wait with M108</summary>
    private static async Task AssertWaitsAsync(Bench bench, HeatManager heat, string code)
    {
        Task<string> waiting = bench.Host.ExecuteCodeAsync(code);
        await bench.CanMaster.WaitUntilAsync(() => heat.IsWaitingForTemperatures, what: $"{code} waiting");
        await Task.Delay(Settle);
        Assert.That(waiting.IsCompleted, Is.False, $"{code} waits for the cold heater");

        await bench.Host.ExecuteCodeAsync("M108");
        Assert.That((await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Trim(), Is.Empty, $"{code} ends with M108");
    }

    /// <summary>
    /// Which heaters each form of M116 waits for, with one heater switched on and cold. Without P, H or C every tool's
    /// heaters and every bed and chamber heater are waited for. P waits for the listed tools, all tools when bare, and
    /// skips a tool that does not exist; H waits for the listed heaters and none when bare; C waits for the listed
    /// chamber slots, all of them when bare, and skips a slot out of range. P, H and C combine, each adding its heaters
    /// </summary>
    /// <remarks>RRF truth: GCodes2.cpp case 116. Regression: m116-wait-for-all-temperatures</remarks>
    [TestCase("M104 T1 S60", "M116", true, TestName = "M116 waits for a tool that is not selected")]
    [TestCase("M140 S60", "M116", true, TestName = "M116 waits for the bed")]
    [TestCase("M141 P1 S60", "M116", true, TestName = "M116 waits for a chamber")]
    [TestCase("M104 T1 S60", "M116 P0", false, TestName = "M116 P0 does not wait for tool 1")]
    [TestCase("M104 T1 S60", "M116 P1", true, TestName = "M116 P1 waits for tool 1")]
    [TestCase("M104 T1 S60", "M116 P0:1", true, TestName = "M116 P0:1 waits for tool 1")]
    [TestCase("M104 T1 S60", "M116 P", true, TestName = "M116 P waits for every tool")]
    [TestCase("M140 S60", "M116 P", false, TestName = "M116 P does not wait for the bed")]
    [TestCase("M104 T1 S60", "M116 P5", false, TestName = "M116 P skips a tool that does not exist")]
    [TestCase("M140 S60", "M116 H5", true, TestName = "M116 H5 waits for heater 5")]
    [TestCase("M140 S60", "M116 H3:4", false, TestName = "M116 H3:4 does not wait for heater 5")]
    [TestCase("M140 S60", "M116 H3:5", true, TestName = "M116 H3:5 waits for heater 5")]
    [TestCase("M140 S60", "M116 H", false, TestName = "M116 H waits for no heater")]
    [TestCase("M140 S60", "M116 H40", false, TestName = "M116 H skips a heater that does not exist")]
    [TestCase("M141 P1 S60", "M116 C0", false, TestName = "M116 C0 does not wait for chamber slot 1")]
    [TestCase("M141 P1 S60", "M116 C1", true, TestName = "M116 C1 waits for chamber slot 1")]
    [TestCase("M141 P1 S60", "M116 C0:1", true, TestName = "M116 C0:1 waits for chamber slot 1")]
    [TestCase("M141 P1 S60", "M116 C", true, TestName = "M116 C waits for every chamber slot")]
    [TestCase("M140 S60", "M116 C", false, TestName = "M116 C does not wait for the bed")]
    [TestCase("M141 P1 S60", "M116 C9", false, TestName = "M116 C skips a slot out of range")]
    [TestCase("M140 S60", "M116 P0 H5", true, TestName = "M116 P0 H5 waits for heater 5")]
    [TestCase("M141 S60", "M116 P1 C0", true, TestName = "M116 P1 C0 waits for chamber slot 0")]
    [TestCase("M140 S60", "M116 P0 C0", false, TestName = "M116 P0 C0 does not wait for the bed")]
    public async Task WaitsFor(string setup, string code, bool waits)
    {
        (Bench bench, HeatManager heat) = await StartAsync(setup);
        await using Bench _ = bench;

        if (waits)
        {
            await AssertWaitsAsync(bench, heat, code);
        }
        else
        {
            Assert.That((await bench.Host.ExecuteCodeAsync(code, timeoutMs: 5_000)).Trim(), Is.Empty, $"{code} does not wait");
        }
    }

    /// <summary>S sets how close a heater has to be to its target, and the default is 1.5C</summary>
    /// <remarks>RRF truth: GCodes2.cpp case 116, TemperatureCloseEnough</remarks>
    [Test]
    public async Task ToleranceIsS()
    {
        (Bench bench, HeatManager heat) = await StartAsync("M140 S60");
        await using Bench _ = bench;
        await bench.ReportHeaterAsync(5, HeaterMode.Heating, 56.0f);

        Assert.That((await bench.Host.ExecuteCodeAsync("M116 S5", timeoutMs: 5_000)).Trim(), Is.Empty, "4C short is within S5");
        await AssertWaitsAsync(bench, heat, "M116");
    }

    /// <summary>S must be greater than zero</summary>
    /// <remarks>RRF truth: GCodeBuffer::GetPositiveFValue</remarks>
    [TestCase("M116 S0")]
    [TestCase("M116 S-1")]
    public async Task RefusesToleranceNotAboveZero(string code)
    {
        (Bench bench, HeatManager _) = await StartAsync("M140 S60");
        await using Bench disposeBench = bench;

        Assert.That(await bench.Host.ExecuteCodeAsync(code, timeoutMs: 5_000), Does.StartWith("Error").And.Contain("value must be greater than zero"));
    }

    /// <summary>A heater above its target is waited for until it has cooled, whichever form is used</summary>
    /// <remarks>RRF truth: every wait in GCodes2.cpp case 116 passes waitWhenCooling true</remarks>
    [TestCase("M116")]
    [TestCase("M116 H5")]
    public async Task WaitsWhileCooling(string code)
    {
        (Bench bench, HeatManager heat) = await StartAsync("M140 S60");
        await using Bench _ = bench;
        await bench.ReportHeaterAsync(5, HeaterMode.Stable, 80.0f);

        await AssertWaitsAsync(bench, heat, code);
    }

    /// <summary>The wait ends when every heater it is waiting for has reached its target</summary>
    [Test]
    public async Task EndsWhenEveryHeaterIsThere()
    {
        (Bench bench, HeatManager heat) = await StartAsync("M104 T0 S60\nM140 S60");
        await using Bench _ = bench;

        Task<string> waiting = bench.Host.ExecuteCodeAsync("M116");
        await bench.CanMaster.WaitUntilAsync(() => heat.IsWaitingForTemperatures, what: "M116 waiting");
        await bench.ReportHeaterAsync(5, HeaterMode.Stable, 60.0f);
        await Task.Delay(Settle);
        Assert.That(waiting.IsCompleted, Is.False, "tool 0 is still cold");

        await bench.ReportHeaterAsync(3, HeaterMode.Stable, 60.0f);
        Assert.That((await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Trim(), Is.Empty, "both there ends the wait");
    }
}
