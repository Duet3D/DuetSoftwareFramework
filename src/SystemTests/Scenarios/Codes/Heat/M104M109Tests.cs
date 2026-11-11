using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Heat;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Heat;

/// <summary>
/// M104 and M109, a tool's temperatures, asserted against RepRapFirmware's GCodes2.cpp case 104/109,
/// GCodes::SetToolHeaters, Tool::SetToolHeaterActiveOrStandbyTemperature, Tool::HeatersToActiveOrStandby and
/// Heat::HeaterAtSetTemperature, and against the regression suite's m104-* and m109-* references, which use heaters 3
/// and 4 on one expansion board
/// </summary>
[TestFixture]
[GCode("M104", "M109")]
public class M104M109Tests : BenchFixture
{
    /// <summary>Tool 0 with heater 3 and tool 1 with heater 4, as the regression cases set them up</summary>
    private static readonly string ToolConfig = $"""
        M308 S3 P"{Expansion.Temp(Machines.DriverBoard, 0)}" Y"thermistor" T100000 B4138
        M308 S4 P"{Expansion.Temp(Machines.DriverBoard, 1)}" Y"thermistor" T100000 B4138
        M950 H3 C"{Expansion.Out(Machines.DriverBoard, 0)}" T3
        M950 H4 C"{Expansion.Out(Machines.DriverBoard, 1)}" T4
        M563 P0 S"tool-zero" H3
        M563 P1 S"tool-one" H4
        """;

    private static Task<Heater> ReadHeaterAsync(Bench bench, int heater)
        => bench.Host.ReadModelAsync(model => model.Heat.Heaters[heater]!);

    private static Task<Tool> ReadToolAsync(Bench bench, int tool)
        => bench.Host.ReadModelAsync(model => model.Tools[tool]!);

    private static (byte Heater, byte Command, byte Function, float SetPoint)[] TemperatureMessages(Bench bench)
        => bench.CanMaster.CanMessages<CanMessageSetHeaterTemperatureV1>()
                .Select(m => (m.Message.HeaterNumber, m.Message.Command, m.Message.Function, m.Message.SetPoint)).ToArray();

    /// <summary>M563 gives a tool's heaters the tool function, and the tool's temperatures start at absolute zero</summary>
    /// <remarks>RRF truth: Tool::Create calls Heat::SetAsToolHeater and sets the temperatures to ABS_ZERO</remarks>
    [Test]
    public async Task ToolDefinitionGivesHeatersToolFunction()
    {
        await using Bench bench = await Bench.StartAsync(ToolConfig);

        Heater heater = await ReadHeaterAsync(bench, 3);
        Tool tool = await ReadToolAsync(bench, 0);
        Assert.Multiple(() =>
        {
            Assert.That(heater.Model.Enabled, Is.True, "the default tool model is enabled");
            Assert.That(heater.Max, Is.EqualTo(285.0f), "monitor 0 is DefaultHotEndTemperatureLimit");
            Assert.That(tool.Active[0], Is.EqualTo(-273.15f), "tools[0].active[0] starts at ABS_ZERO");
            Assert.That(tool.Standby[0], Is.EqualTo(-273.15f), "tools[0].standby[0] starts at ABS_ZERO");
        });
    }

    /// <summary>
    /// M104 S with no tool selected sets the lowest-numbered tool's active and standby temperatures and puts it on
    /// standby, which switches its heater on at the standby temperature with one message
    /// </summary>
    /// <remarks>Regression: m104-set-tool-temperature</remarks>
    [Test]
    public async Task SetsDefaultToolOnStandby()
    {
        await using Bench bench = await Bench.StartAsync(ToolConfig);

        Assert.That((await bench.Host.ExecuteCodeAsync("M104 S40")).Trim(), Is.Empty, "M104 replies nothing");
        Heater heater = await ReadHeaterAsync(bench, 3);
        Tool tool = await ReadToolAsync(bench, 0);
        Assert.Multiple(async () =>
        {
            Assert.That(TemperatureMessages(bench), Is.EqualTo(new[] { ((byte)3, CanMessageSetHeaterTemperatureV1.CommandOn, (byte)HeaterFunction.Tool, 40.0f) }),
                        "one setHeaterTemperature switches the heater on at 40C");
            Assert.That(tool.Active[0], Is.EqualTo(40.0f), "tools[0].active[0]");
            Assert.That(tool.Standby[0], Is.EqualTo(40.0f), "tools[0].standby[0]");
            Assert.That(tool.State, Is.EqualTo(ToolState.Standby), "tools[0].state");
            Assert.That(heater.Active, Is.EqualTo(40.0f), "heaters[3].active");
            Assert.That(heater.Standby, Is.EqualTo(40.0f), "heaters[3].standby");
            Assert.That(heater.State, Is.EqualTo(HeaterState.Standby), "heaters[3].state");
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(-1), "M104 selects nothing");
        });
    }

    /// <summary>A heater M104 put on standby still reads standby when its board reports it heating</summary>
    /// <remarks>RRF truth: Heater::GetStatus reports standby for a heater in a PID mode that is not active</remarks>
    [Test]
    public async Task StandbyHeaterStaysStandbyWhenBoardReportsHeating()
    {
        await using Bench bench = await Bench.StartAsync(ToolConfig);

        await bench.Host.ExecuteCodeAsync("M104 S40");
        await bench.ReportHeaterAsync(3, HeaterMode.Heating, 25.0f);
        Assert.That((await ReadHeaterAsync(bench, 3)).State, Is.EqualTo(HeaterState.Standby), "heaters[3].state");
    }

    /// <summary>M104 T addresses that tool, and R is a synonym for S</summary>
    /// <remarks>Regression: m104-select-tool-with-t, m104-undocumented-r-parameter</remarks>
    [TestCase("M104 S40 T1", 1, 4, 40.0f)]
    [TestCase("M104 R45", 0, 3, 45.0f)]
    public async Task AddressesToolAndTakesR(string code, int toolNumber, int heaterNumber, float temperature)
    {
        await using Bench bench = await Bench.StartAsync(ToolConfig);

        await bench.Host.ExecuteCodeAsync(code);
        Tool tool = await ReadToolAsync(bench, toolNumber);
        Assert.Multiple(() =>
        {
            Assert.That(TemperatureMessages(bench), Is.EqualTo(new[] { ((byte)heaterNumber, CanMessageSetHeaterTemperatureV1.CommandOn, (byte)HeaterFunction.Tool, temperature) }),
                        "the tool's heater is switched on");
            Assert.That(tool.Active[0], Is.EqualTo(temperature), "active");
            Assert.That(tool.Standby[0], Is.EqualTo(temperature), "standby");
            Assert.That(tool.State, Is.EqualTo(ToolState.Standby), "state");
        });
    }

    /// <summary>
    /// M104 with neither S nor R does nothing, a tool that does not exist is refused, and a temperature at or above the
    /// heater's over-temperature limit is refused before anything changes
    /// </summary>
    /// <remarks>RRF truth: GCodes2.cpp case 104 and Tool::SetToolHeaterActiveOrStandbyTemperature</remarks>
    [TestCase("M104", null)]
    [TestCase("M104 T5 S40", "Invalid tool number")]
    [TestCase("M104 S285", "Requested temperature out of range")]
    public async Task RefusesOrIgnores(string code, string? error)
    {
        await using Bench bench = await Bench.StartAsync(ToolConfig);

        string reply = await bench.Host.ExecuteCodeAsync(code);
        Tool tool = await ReadToolAsync(bench, 0);
        Assert.Multiple(() =>
        {
            if (error is null)
            {
                Assert.That(reply.Trim(), Is.Empty, "accepted");
            }
            else
            {
                Assert.That(reply, Does.StartWith("Error").And.Contain(error), "refused");
            }
            Assert.That(TemperatureMessages(bench), Is.Empty, "nothing is sent");
            Assert.That(tool.Active[0], Is.EqualTo(-273.15f), "the tool is unchanged");
            Assert.That(tool.State, Is.EqualTo(ToolState.Off), "the tool stays off");
        });
    }

    /// <summary>
    /// M104 at or below -273 zeroes the tool's temperatures and switches the heater off twice, once for each, and then
    /// putting the tool on standby switches it back on at 0
    /// </summary>
    /// <remarks>RRF truth: Tool::SetToolHeaterActiveOrStandbyTemperature, then Tool::Standby</remarks>
    [Test]
    public async Task BelowAbsoluteZeroOnUnselectedTool()
    {
        await using Bench bench = await Bench.StartAsync(ToolConfig);

        await bench.Host.ExecuteCodeAsync("M104 S-273.1");
        Tool tool = await ReadToolAsync(bench, 0);
        Assert.Multiple(() =>
        {
            Assert.That(TemperatureMessages(bench).Select(m => (m.Command, m.SetPoint)),
                        Is.EqualTo(new[] { (CanMessageSetHeaterTemperatureV1.CommandOff, 0.0f), (CanMessageSetHeaterTemperatureV1.CommandOff, 0.0f), (CanMessageSetHeaterTemperatureV1.CommandOn, 0.0f) }),
                        "off for active, off for standby, then on at the standby temperature");
            Assert.That(tool.Active[0], Is.EqualTo(0.0f), "tools[0].active[0] is 0");
            Assert.That(tool.Standby[0], Is.EqualTo(0.0f), "tools[0].standby[0] is 0");
        });
    }

    /// <summary>
    /// M104 on the selected tool updates its heater, which is already active, and switches it to active again: three
    /// messages
    /// </summary>
    /// <remarks>RRF truth: Heater::SetTemperature resends for the active temperature, then MovementState::SelectTool re-activates</remarks>
    [Test]
    public async Task SelectedToolIsActivatedAgain()
    {
        await using Bench bench = await Bench.StartAsync(ToolConfig + "\nT0");

        await bench.Host.ExecuteCodeAsync("M104 S50");
        Assert.Multiple(async () =>
        {
            Assert.That(TemperatureMessages(bench).Select(m => (m.Command, m.SetPoint)),
                        Is.EqualTo(Enumerable.Repeat((CanMessageSetHeaterTemperatureV1.CommandOn, 50.0f), 3)), "three messages at 50C");
            Assert.That((await ReadToolAsync(bench, 0)).State, Is.EqualTo(ToolState.Active), "the tool stays active");
            Assert.That((await ReadHeaterAsync(bench, 3)).State, Is.EqualTo(HeaterState.Active), "the heater is active");
        });
    }

    /// <summary>
    /// A heater shared with the selected tool keeps its active temperature when another tool sets one, and is not switched
    /// to standby. It takes the standby temperature only if no other tool last put it on standby
    /// </summary>
    /// <remarks>
    /// RRF truth: Tool::SetToolHeaterActiveOrStandbyTemperature and Tool::HeatersToActiveOrStandby skip a heater
    /// IsHeaterUsedByDifferentCurrentTool, except the standby temperature when Heat::GetLastStandbyTool is none or this tool
    /// </remarks>
    [TestCase("M568 P0 S50 R50", 60.0f, TestName = "SharedHeaterTakesStandbyNoToolPutOnStandby")]
    [TestCase("M104 T0 S50", 50.0f, TestName = "SharedHeaterKeepsStandbyAnotherToolPutOnStandby")]
    public async Task SharedHeaterIsLeftToSelectedTool(string setup, float standby)
    {
        await using Bench bench = await Bench.StartAsync(ToolConfig + $"\nM563 P2 H3\n{setup}\nT0");

        await bench.Host.ExecuteCodeAsync("M104 T2 S60");
        Heater heater = await ReadHeaterAsync(bench, 3);
        Assert.Multiple(async () =>
        {
            Assert.That(TemperatureMessages(bench), Is.Empty, "nothing is sent");
            Assert.That(heater.Active, Is.EqualTo(50.0f), "heaters[3].active is tool 0's");
            Assert.That(heater.Standby, Is.EqualTo(standby), "heaters[3].standby");
            Assert.That(heater.State, Is.EqualTo(HeaterState.Active), "heaters[3] stays active");
            Assert.That((await ReadToolAsync(bench, 2)).State, Is.EqualTo(ToolState.Standby), "tool 2 is on standby");
        });
    }

    /// <summary>
    /// M109 with no tool selected sets the temperatures and selects the tool, which switches its heater on, and does not
    /// wait for a target below 40C
    /// </summary>
    /// <remarks>Regression: m109-set-and-wait</remarks>
    [Test]
    public async Task SelectsToolWhenNoneIsSelected()
    {
        await using Bench bench = await Bench.StartAsync(ToolConfig);

        Assert.That((await bench.Host.ExecuteCodeAsync("M109 S0")).Trim(), Is.Empty, "M109 replies nothing");
        Assert.Multiple(async () =>
        {
            Assert.That(TemperatureMessages(bench), Is.EqualTo(new[] { ((byte)3, CanMessageSetHeaterTemperatureV1.CommandOn, (byte)HeaterFunction.Tool, 0.0f) }),
                        "the heater is switched on once, at 0C");
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(0), "tool 0 is selected");
            Assert.That((await ReadToolAsync(bench, 0)).State, Is.EqualTo(ToolState.Active), "tools[0].state");
            Assert.That((await ReadHeaterAsync(bench, 3)).State, Is.EqualTo(HeaterState.Active), "heaters[3].state");
        });
    }

    /// <summary>
    /// M109 with no tool selected sets the temperatures, selects the tool and then waits for its heater to reach a
    /// target of 40C or more
    /// </summary>
    /// <remarks>RRF truth: GCodeState::m109ToolChange0, then GCodeState::m109WaitForTemperature</remarks>
    [Test]
    public async Task SelectsToolThenWaitsWhenNoneIsSelected()
    {
        await using Bench bench = await Bench.StartAsync(ToolConfig);
        HeatManager heat = bench.Host.Services.GetRequiredService<HeatManager>();
        await bench.ReportHeaterAsync(3, HeaterMode.Heating, 25.0f);

        Task<string> waiting = bench.Host.ExecuteCodeAsync("M109 S50");
        await bench.CanMaster.WaitUntilAsync(() => heat.IsWaitingForTemperatures, what: "M109 waiting");
        Tool tool = await ReadToolAsync(bench, 0);
        Heater heater = await ReadHeaterAsync(bench, 3);
        Assert.Multiple(async () =>
        {
            Assert.That(waiting.IsCompleted, Is.False, "the cold heater is waited for");
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(0), "tool 0 is selected before the wait");
            Assert.That(tool.State, Is.EqualTo(ToolState.Active), "tools[0].state");
            Assert.That(tool.Active[0], Is.EqualTo(50.0f), "tools[0].active[0]");
            Assert.That(tool.Standby[0], Is.EqualTo(50.0f), "tools[0].standby[0]");
            Assert.That(heater.Active, Is.EqualTo(50.0f), "heaters[3].active");
            Assert.That(heater.State, Is.EqualTo(HeaterState.Active), "heaters[3].state");
        });

        await bench.ReportHeaterAsync(3, HeaterMode.Stable, 50.0f);
        Assert.That((await waiting).Trim(), Is.Empty, "reaching the target ends the wait");
    }

    /// <summary>M109 S waits for the selected tool to come up to temperature, and not for it to cool down</summary>
    /// <remarks>RRF truth: GCodeState::m109WaitForTemperature, ToolHeatersAtSetTemperatures with waitWhileCooling false</remarks>
    [Test]
    public async Task WaitsForHeatingOnly()
    {
        await using Bench bench = await Bench.StartAsync(ToolConfig + "\nT0");
        HeatManager heat = bench.Host.Services.GetRequiredService<HeatManager>();
        await bench.ReportHeaterAsync(3, HeaterMode.Heating, 25.0f);

        Task<string> waiting = bench.Host.ExecuteCodeAsync("M109 S50");
        await bench.CanMaster.WaitUntilAsync(() => heat.IsWaitingForTemperatures, what: "M109 waiting");
        await bench.ReportHeaterAsync(3, HeaterMode.Heating, 48.0f);
        Assert.That(waiting.IsCompleted, Is.False, "2C short is still waited for");
        await bench.ReportHeaterAsync(3, HeaterMode.Stable, 49.0f);
        Assert.That((await waiting).Trim(), Is.Empty, "within 1.5C ends the wait");

        await bench.ReportHeaterAsync(3, HeaterMode.Stable, 80.0f);
        Assert.That((await bench.Host.ExecuteCodeAsync("M109 S50")).Trim(), Is.Empty, "a heater above an S target is not waited for");
    }

    /// <summary>M109 R waits for the selected tool to cool down to the target as well</summary>
    /// <remarks>RRF truth: R sets waitWhileCooling. Regression: m109-set-and-wait-with-r-and-tool</remarks>
    [Test]
    public async Task RWaitsWhileCooling()
    {
        await using Bench bench = await Bench.StartAsync(ToolConfig + "\nT0");
        HeatManager heat = bench.Host.Services.GetRequiredService<HeatManager>();
        await bench.ReportHeaterAsync(3, HeaterMode.Stable, 80.0f);

        Task<string> waiting = bench.Host.ExecuteCodeAsync("M109 R50");
        await bench.CanMaster.WaitUntilAsync(() => heat.IsWaitingForTemperatures, what: "M109 R waiting");
        Assert.That(waiting.IsCompleted, Is.False, "a heater above an R target is waited for");
        await bench.ReportHeaterAsync(3, HeaterMode.Stable, 51.0f);
        Assert.That((await waiting).Trim(), Is.Empty, "within 1.5C ends the wait");
    }

    /// <summary>M109 T for a tool other than the selected one puts it on standby and waits for the selected tool</summary>
    /// <remarks>RRF truth: GCodeState::m109WaitForTemperature waits on ms.currentTool</remarks>
    [Test]
    public async Task WaitsForSelectedToolNotT()
    {
        await using Bench bench = await Bench.StartAsync(ToolConfig + "\nT0");
        await bench.ReportHeaterAsync(4, HeaterMode.Heating, 25.0f);

        Assert.That((await bench.Host.ExecuteCodeAsync("M109 T1 S50")).Trim(), Is.Empty,
                    "tool 1 is cold but tool 0, at 0C, is what is waited for");
        Assert.That((await ReadToolAsync(bench, 1)).State, Is.EqualTo(ToolState.Standby), "tool 1 is on standby");
    }
}
