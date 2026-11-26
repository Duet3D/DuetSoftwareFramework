using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuetControlServer.Heat;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Spindles;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Tools.ToolSetup;

namespace SystemTests.Scenarios.Codes.Tools;

/// <summary>
/// M568, a tool's temperatures, heater states and spindle RPM, asserted against RepRapFirmware's
/// GCodes::SetOrReportOffsets with code 568 (GCodes.cpp), GCodes::GetSpecifiedOrCurrentTool,
/// Tool::SetToolHeaterActiveOrStandbyTemperature, Tool::HeatersToActiveOrStandby, Tool::HeatersToOff,
/// Tool::SetSpindleRpm, Heater::SetTemperature and RemoteHeater::SwitchOn/SwitchOff, and against the
/// regression suite's m568-* references
/// </summary>
[TestFixture]
[GCode("M568")]
public class M568Tests : BenchFixture
{
    /// <summary>
    /// M568 sets the tool's active and standby temperatures, pushes them to the mapped heater's
    /// setpoints, and A moves the heater between off, standby and active
    /// </summary>
    /// <remarks>
    /// GCodes::SetOrReportOffsets with code 568 (GCodes.cpp): S/R go through
    /// Tool::SetToolHeaterActiveOrStandbyTemperature, which also sets the heater setpoint when no
    /// other current tool uses the heater; A drives Tool::HeatersToOff/HeatersToActiveOrStandby.
    /// The heater's reported state combines the board-reported mode with the local active flag
    /// (Heater::GetStatus, Heater.cpp), so the test plays the board's status reports
    /// </remarks>
    [Test]
    public async Task M568SetsTemperaturesAndHeaterStates()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, HeaterToolConfig);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M568 P0 S210 R180");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.Active[0]), Is.EqualTo(210), "M568 S sets tools[0].active (RRF SetToolHeaterActiveTemperature)");
            Assert.That(await host.ReadModelAsync(model => model.Tools[0]!.Standby[0]), Is.EqualTo(180), "M568 R sets tools[0].standby (RRF SetToolHeaterStandbyTemperature)");
            Assert.That(await host.ReadModelAsync(model => model.Heat.Heaters[0]!.Active), Is.EqualTo(210), "the tool temperature reaches the heater setpoint (RRF Heat::SetTemperature)");
            Assert.That(await host.ReadModelAsync(model => model.Heat.Heaters[0]!.Standby), Is.EqualTo(180), "the tool temperature reaches the heater setpoint (RRF Heat::SetTemperature)");
        });

        await host.ExecuteCodeAsync("M568 P0 A2");
        bench.CanMaster.InjectHeatersStatus(srcAddress: Machines.DriverBoard, heaterNumber: 0, HeaterMode.Heating, currentTemperature: 30.0f);
        Assert.That(await host.SettledValueAsync(model => model.Heat.Heaters[0]!.State, HeaterState.Active), Is.EqualTo(HeaterState.Active),
                    "M568 A2 puts the heater in active (RRF Tool::HeatersToActiveOrStandby, Heater::GetStatus)");

        await host.ExecuteCodeAsync("M568 P0 A1");
        Assert.That(await host.SettledValueAsync(model => model.Heat.Heaters[0]!.State, HeaterState.Standby), Is.EqualTo(HeaterState.Standby),
                    "M568 A1 puts the heater in standby (RRF Tool::HeatersToActiveOrStandby, Heater::GetStatus)");

        await host.ExecuteCodeAsync("M568 P0 A0");
        bench.CanMaster.InjectHeatersStatus(srcAddress: Machines.DriverBoard, heaterNumber: 0, HeaterMode.Off, currentTemperature: 30.0f);
        Assert.That(await host.SettledValueAsync(model => model.Heat.Heaters[0]!.State, HeaterState.Off), Is.EqualTo(HeaterState.Off),
                    "M568 A0 switches the heater off (RRF Tool::HeatersToOff, Heater::GetStatus)");

        string reply = (await host.ExecuteCodeAsync("M568 P0")).Trim();
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Tool 0"), "bare M568 reports the tool (RRF SetOrReportOffsets)");
            Assert.That(reply, Reply.IsSuccess("Tool 0 is Off, active temperatures: 210.0, standby temperatures: 180.0"), "bare M568 reports the model temperatures (RRF SetOrReportOffsets)");
        });
    }

    /// <summary>Tool 0's active temperature as the scenarios configure it, the regression cases' value</summary>
    private const float ToolActive = 205.0f;

    /// <summary>Tool 0's standby temperature as the scenarios configure it, the regression cases' value</summary>
    private const float ToolStandby = 140.0f;

    /// <summary>A heater's active and standby setpoints before anything sets them</summary>
    private const float UnsetHeaterTemperature = 0.0f;

    /// <summary>The over-temperature limit of a tool heater with the default monitor, at and above which M568 refuses a temperature</summary>
    private const float ToolHeaterLimit = HeatManager.DefaultHotEndTemperatureLimit;

    /// <summary>Sensors and heaters 0 to count-1 on <see cref="Machines.DriverBoard"/>, heater n on temp n and out n</summary>
    private static string Heaters(int count)
        => string.Join('\n', Enumerable.Range(0, count).Select(heater => $"""
            M308 S{heater} P"{Expansion.Temp(Machines.DriverBoard, heater)}" Y"thermistor" T100000 B4138
            M950 H{heater} C"{Expansion.Out(Machines.DriverBoard, heater)}" T{heater}
            """));

    /// <summary>Tool 0 with heater 0 and no drives, so that no machine is needed</summary>
    private static readonly string HeaterTool = Heaters(1) + "\nM563 P0 S\"Primary\" H0";

    /// <summary><see cref="HeaterTool"/> with tool 0's temperatures set</summary>
    private static readonly string HeaterToolWithTemperatures = HeaterTool + $"\nM568 P0 S{ToolActive} R{ToolStandby}";

    /// <summary>Spindle 0 on <see cref="Machines.DriverBoard"/> with its default RPM range, and tool 0 using it</summary>
    private static readonly string SpindleToolConfig = $"""
        M950 R0 C"{Expansion.Out(Machines.DriverBoard, 6)}"
        M563 P0 S"Cutter" R0
        """;

    /// <summary>Every heater setpoint message sent, with the board it went to</summary>
    private static (byte Board, byte Heater, byte Command, byte Function, float SetPoint)[] TemperatureMessages(Bench bench)
        => [.. bench.CanMaster.CanMessages<CanMessageSetHeaterTemperatureV1>()
                .Select(m => (m.Board, m.Message.HeaterNumber, m.Message.Command, m.Message.Function, m.Message.SetPoint))];

    /// <summary>One heater setpoint message to a tool heater on <see cref="Machines.DriverBoard"/></summary>
    private static (byte Board, byte Heater, byte Command, byte Function, float SetPoint) ToolHeaterMessage(int heater, byte command, float setPoint)
        => (Machines.DriverBoard, (byte)heater, command, (byte)HeaterFunction.Tool, setPoint);

    /// <summary>Every general-purpose output write sent, with the board it went to</summary>
    private static (byte Board, byte Port, bool IsServo, float Pwm)[] GpioWrites(Bench bench)
        => [.. bench.CanMaster.CanMessages<CanMessageWriteGpio>()
                .Select(w => (w.Board, w.Message.PortNumber, w.Message.IsServo, MathF.Round(w.Message.Pwm, 4)))];

    private static Task<Tool> ReadToolAsync(Bench bench, int tool)
        => bench.Host.ReadModelAsync(model => model.Tools[tool]!);

    private static Task<Heater> ReadHeaterAsync(Bench bench, int heater)
        => bench.Host.ReadModelAsync(model => model.Heat.Heaters[heater]!);

    private static Task<Spindle> ReadSpindleAsync(Bench bench)
        => bench.Host.ReadModelAsync(model => model.Spindles[0]!);

    /// <summary>
    /// A temperature for a heater that is off is stored on the tool and as the heater's setpoint and nothing is
    /// sent, and S0 or S1, which once switched mixing off or on, is a temperature like any other
    /// </summary>
    /// <remarks>
    /// RRF truth: Tool::SetToolHeaterActiveOrStandbyTemperature calls Heat::SetTemperature when no other current tool
    /// uses the heater, and Heater::SetTemperature only switches the heater on again when its mode is above suspended
    /// and the temperature is for the state it is in. Regression: m568-set-tool-settings, m568-deprecated-mix-toggle
    /// </remarks>
    [TestCase("M568 P0 S180", 180.0f, ToolStandby)]
    [TestCase("M568 P0 R120", ToolActive, 120.0f)]
    [TestCase("M568 P0 S0", 0.0f, ToolStandby)]
    [TestCase("M568 P0 S1", 1.0f, ToolStandby)]
    public async Task TemperatureForAnOffHeaterIsStoredWithoutReachingTheBoard(string code, float active, float standby)
    {
        await using Bench bench = await Bench.StartAsync(HeaterToolWithTemperatures);

        Assert.That(await bench.Host.ExecuteCodeAsync(code), Reply.IsEmpty, "M568 setting something replies nothing");
        Tool tool = await ReadToolAsync(bench, 0);
        Heater heater = await ReadHeaterAsync(bench, 0);
        Assert.Multiple(() =>
        {
            Assert.That(tool.Active[0], Is.EqualTo(active), "tools[0].active[0]");
            Assert.That(tool.Standby[0], Is.EqualTo(standby), "tools[0].standby[0]");
            Assert.That(heater.Active, Is.EqualTo(active), "heaters[0].active");
            Assert.That(heater.Standby, Is.EqualTo(standby), "heaters[0].standby");
            Assert.That(heater.State, Is.EqualTo(HeaterState.Off), "heaters[0] stays off");
            Assert.That(tool.State, Is.EqualTo(ToolState.Off), "tools[0] stays off");
            Assert.That(TemperatureMessages(bench), Is.Empty, "an off heater is not sent its setpoint");
        });
    }

    /// <summary>
    /// With no P, M568 addresses the current tool. A temperature for the state the heater is in is sent to the board
    /// at once, one message and no re-activation; a temperature for the other state is only stored
    /// </summary>
    /// <remarks>
    /// RRF truth: GetSpecifiedOrCurrentTool; Heater::SetTemperature sends CanMessageSetHeaterTemperature commandOn
    /// through RemoteHeater::SwitchOn only when active matches the heater's state; A1 switches the heater, not the tool,
    /// to standby (Tool::HeatersToActiveOrStandby)
    /// </remarks>
    [Test]
    public async Task SetpointReachesTheBoardOnlyForTheHeatersCurrentState()
    {
        await using Bench bench = await Bench.StartAsync(HeaterToolWithTemperatures + "\nT0");
        await bench.ReportHeaterAsync(0, HeaterMode.Heating, 25.0f);

        await bench.Host.ExecuteCodeAsync("M568 S220");
        Assert.Multiple(async () =>
        {
            Assert.That(TemperatureMessages(bench), Is.EqualTo(new[] { ToolHeaterMessage(0, CanMessageSetHeaterTemperatureV1.CommandOn, 220.0f) }),
                        "the active heater gets its new setpoint in one message");
            Assert.That((await ReadToolAsync(bench, 0)).Active[0], Is.EqualTo(220.0f), "no P sets the current tool's tools[0].active[0]");
            Assert.That((await ReadHeaterAsync(bench, 0)).Active, Is.EqualTo(220.0f), "heaters[0].active");
        });

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync("M568 R150");
        Assert.Multiple(async () =>
        {
            Assert.That(TemperatureMessages(bench), Is.Empty, "a standby temperature is not sent to an active heater");
            Assert.That((await ReadToolAsync(bench, 0)).Standby[0], Is.EqualTo(150.0f), "tools[0].standby[0]");
            Assert.That((await ReadHeaterAsync(bench, 0)).Standby, Is.EqualTo(150.0f), "heaters[0].standby");
        });

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync("M568 A1");
        await bench.ReportHeaterAsync(0, HeaterMode.Heating, 26.0f);
        Assert.Multiple(async () =>
        {
            Assert.That(TemperatureMessages(bench), Is.EqualTo(new[] { ToolHeaterMessage(0, CanMessageSetHeaterTemperatureV1.CommandOn, 150.0f) }),
                        "A1 switches the heater to its standby temperature");
            Assert.That((await ReadHeaterAsync(bench, 0)).State, Is.EqualTo(HeaterState.Standby), "heaters[0].state");
            Assert.That((await ReadToolAsync(bench, 0)).State, Is.EqualTo(ToolState.Active), "A changes the heaters, not tools[0].state");
        });

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync("M568 R140");
        Assert.That(TemperatureMessages(bench), Is.EqualTo(new[] { ToolHeaterMessage(0, CanMessageSetHeaterTemperatureV1.CommandOn, 140.0f) }),
                    "a heater on standby gets a new standby temperature at once");

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync("M568 S230");
        Assert.Multiple(async () =>
        {
            Assert.That(TemperatureMessages(bench), Is.Empty, "an active temperature is not sent to a heater on standby");
            Assert.That((await ReadHeaterAsync(bench, 0)).Active, Is.EqualTo(230.0f), "heaters[0].active");
        });
    }

    /// <summary>Codes M568 refuses, none of which changes or sends anything</summary>
    private static readonly string[] RefusedCodes =
    [
        "M568",                                     // no P and no current tool
        "M568 S200",
        "M568 P5",                                  // no such tool
        "M568 P5 S200",
        "M568 P0 S200:210",                         // more values than the tool has heaters
        "M568 P0 A3",                               // A is 0, 1 or 2
        "M568 P0 A-1",
        $"M568 P0 S{ToolHeaterLimit}",              // at the heater's limit
        $"M568 P0 R{ToolHeaterLimit}",
        $"M568 P0 S{ToolHeaterLimit + 10}",
    ];

    /// <summary>
    /// No tool to address, a value list longer than the tool's heaters, an A outside 0 to 2 and a temperature at or
    /// above the heater's limit are refused before anything changes
    /// </summary>
    /// <remarks>
    /// RRF truth: GetSpecifiedOrCurrentTool throws "No tool number given and no current tool" / "Invalid tool number";
    /// StringParser::CheckArrayLength throws "array too long"; TryGetLimitedUIValue('A', ..., 3) throws; and
    /// Tool::SetToolHeaterActiveOrStandbyTemperature throws "Requested temperature out of range" for temp &gt;= the
    /// heater's highest limit
    /// </remarks>
    [TestCaseSource(nameof(RefusedCodes))]
    public async Task RefusedWithoutChangingAnything(string code)
    {
        await using Bench bench = await Bench.StartAsync(HeaterTool);

        string reply = await bench.Host.ExecuteCodeAsync(code);
        Tool tool = await ReadToolAsync(bench, 0);
        Heater heater = await ReadHeaterAsync(bench, 0);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Reply.IsError(), "refused");
            Assert.That(tool.Active[0], Is.EqualTo(HeatManager.AbsoluteZero), "tools[0].active[0] is unchanged");
            Assert.That(tool.Standby[0], Is.EqualTo(HeatManager.AbsoluteZero), "tools[0].standby[0] is unchanged");
            Assert.That(heater.Active, Is.EqualTo(UnsetHeaterTemperature), "heaters[0].active is unchanged");
            Assert.That(heater.Standby, Is.EqualTo(UnsetHeaterTemperature), "heaters[0].standby is unchanged");
            Assert.That(heater.State, Is.EqualTo(HeaterState.Off), "heaters[0] stays off");
            Assert.That(TemperatureMessages(bench), Is.Empty, "nothing is sent");
        });
    }

    /// <summary>
    /// A tool with several heaters takes one temperature per heater as a colon list; a single value applies to every
    /// heater, and a shorter list sets only the heaters it reaches. The report lists every heater's temperatures
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodeBuffer::GetFloatArray with doPad pads only a single value, and returns the length read, which
    /// bounds the loop over Tool::SetToolHeaterActiveOrStandbyTemperature; the report prints each heater's
    /// active/standby pair
    /// </remarks>
    [TestCase("S200:210:220 R150:160:170", new[] { 200.0f, 210.0f, 220.0f }, new[] { 150.0f, 160.0f, 170.0f })]
    [TestCase("S200 R150", new[] { 200.0f, 200.0f, 200.0f }, new[] { 150.0f, 150.0f, 150.0f })]
    [TestCase("S200:210", new[] { 200.0f, 210.0f, HeatManager.AbsoluteZero }, new[] { HeatManager.AbsoluteZero, HeatManager.AbsoluteZero, HeatManager.AbsoluteZero })]
    public async Task ColonListSetsEachHeaterOfTheTool(string parameters, float[] active, float[] standby)
    {
        await using Bench bench = await Bench.StartAsync(Heaters(3) + "\nM563 P0 S\"Triple\" H0:1:2");

        Assert.That(await bench.Host.ExecuteCodeAsync($"M568 P0 {parameters}"), Reply.IsEmpty, "accepted");
        Tool tool = await ReadToolAsync(bench, 0);
        float[] heatersActive = await bench.Host.ReadModelAsync(model => model.Heat.Heaters.Take(3).Select(h => h!.Active).ToArray());
        float[] heatersStandby = await bench.Host.ReadModelAsync(model => model.Heat.Heaters.Take(3).Select(h => h!.Standby).ToArray());
        static float OnHeater(float toolTemperature) => (toolTemperature <= HeatManager.NearlyAbsoluteZero) ? UnsetHeaterTemperature : toolTemperature;
        Assert.Multiple(() =>
        {
            Assert.That(tool.Active, Is.EqualTo(active), "tools[0].active");
            Assert.That(tool.Standby, Is.EqualTo(standby), "tools[0].standby");
            Assert.That(heatersActive, Is.EqualTo(active.Select(OnHeater)), "heaters[0..2].active");
            Assert.That(heatersStandby, Is.EqualTo(standby.Select(OnHeater)), "heaters[0..2].standby");
            Assert.That(TemperatureMessages(bench), Is.Empty, "the heaters are off, so nothing is sent");
        });

        string report = await bench.Host.ExecuteCodeAsync("M568 P0");
        foreach (float temperature in active.Concat(standby).Where(t => t > HeatManager.NearlyAbsoluteZero).Distinct())
        {
            Assert.That(report, Reply.IsSuccess(temperature.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)),
                        $"the report lists {temperature} among the tool's temperatures");
        }
    }

    /// <summary>
    /// A temperature at or below -273 zeroes the tool's temperature and switches the heater off, leaving the heater's
    /// own setpoints alone
    /// </summary>
    /// <remarks>
    /// RRF truth: Tool::SetToolHeaterActiveOrStandbyTemperature sets the tool's temperature to 0 and calls
    /// Heat::SwitchOff, and RemoteHeater::SwitchOff sends commandOff with the heater's target, its standby temperature
    /// as it has never been made active
    /// </remarks>
    [TestCase('S')]
    [TestCase('R')]
    public async Task AtAbsoluteZeroTheToolTemperatureIsZeroedAndTheHeaterSwitchedOff(char letter)
    {
        await using Bench bench = await Bench.StartAsync(HeaterToolWithTemperatures);

        Assert.That(await bench.Host.ExecuteCodeAsync($"M568 P0 {letter}-274"), Reply.IsEmpty, "accepted");
        Tool tool = await ReadToolAsync(bench, 0);
        Heater heater = await ReadHeaterAsync(bench, 0);
        Assert.Multiple(() =>
        {
            Assert.That(tool.Active[0], Is.EqualTo((letter == 'S') ? 0.0f : ToolActive), "tools[0].active[0]");
            Assert.That(tool.Standby[0], Is.EqualTo((letter == 'R') ? 0.0f : ToolStandby), "tools[0].standby[0]");
            Assert.That(TemperatureMessages(bench), Is.EqualTo(new[] { ToolHeaterMessage(0, CanMessageSetHeaterTemperatureV1.CommandOff, ToolStandby) }),
                        "the heater is switched off, naming its target");
            Assert.That(heater.Active, Is.EqualTo(ToolActive), "heaters[0].active is left alone");
            Assert.That(heater.Standby, Is.EqualTo(ToolStandby), "heaters[0].standby is left alone");
            Assert.That(heater.State, Is.EqualTo(HeaterState.Off), "heaters[0].state");
        });
    }

    /// <summary>
    /// A1, A2 and A0 each send one setpoint message with the tool's temperature for that state, and leave the tool's
    /// own state alone
    /// </summary>
    /// <remarks>
    /// RRF truth: Tool::HeatersToActiveOrStandby calls Heat::SetTemperature (nothing sent, as the state differs) then
    /// Heat::SetActiveOrStandby, whose RemoteHeater::SwitchOn sends commandOn at the target; Tool::HeatersToOff calls
    /// RemoteHeater::SwitchOff, commandOff at the target. Regression: m568-enable-tool-heaters
    /// </remarks>
    [Test]
    public async Task HeaterStateSendsTheToolsTemperatureAndLeavesTheToolState()
    {
        await using Bench bench = await Bench.StartAsync(HeaterToolWithTemperatures);

        (string Code, byte Command, float SetPoint)[] steps =
        [
            ("M568 P0 A1", CanMessageSetHeaterTemperatureV1.CommandOn, ToolStandby),
            ("M568 P0 A2", CanMessageSetHeaterTemperatureV1.CommandOn, ToolActive),
            ("M568 P0 A0", CanMessageSetHeaterTemperatureV1.CommandOff, ToolActive),
        ];
        float reading = 25.0f;
        foreach ((string code, byte command, float setPoint) in steps)
        {
            bench.CanMaster.ClearCapture();
            Assert.That(await bench.Host.ExecuteCodeAsync(code), Reply.IsEmpty, $"{code} replies nothing");
            await bench.ReportHeaterAsync(0, (command == CanMessageSetHeaterTemperatureV1.CommandOn) ? HeaterMode.Heating : HeaterMode.Off, reading++);
            Assert.Multiple(async () =>
            {
                Assert.That(TemperatureMessages(bench), Is.EqualTo(new[] { ToolHeaterMessage(0, command, setPoint) }), $"{code} sends one setpoint message");
                Assert.That((await ReadToolAsync(bench, 0)).State, Is.EqualTo(ToolState.Off), $"{code} leaves tools[0].state off");
            });
        }
    }

    /// <summary>
    /// When another tool is current, M568 on a tool sharing one of its heaters leaves that heater to the current tool:
    /// A switches only the other heaters and a new active temperature is only stored
    /// </summary>
    /// <remarks>
    /// RRF truth: Tool::HeatersToActiveOrStandby, Tool::HeatersToOff and Tool::SetToolHeaterActiveOrStandbyTemperature
    /// skip a heater GCodes::IsHeaterUsedByDifferentCurrentTool; the wiki: "If there is a current tool and the P
    /// parameter specifies a different tool, then any heaters used by the current tool are not affected"
    /// </remarks>
    [Test]
    public async Task HeatersOfADifferentCurrentToolAreLeftAlone()
    {
        await using Bench bench = await Bench.StartAsync($"""
            {Heaters(2)}
            M563 P0 S"Single" H0
            M563 P1 S"Shared" H0:1
            M568 P1 S215:225 R150:160
            M568 P0 S{ToolActive} R{ToolStandby}
            T0
            """);
        await bench.ReportHeaterAsync(0, HeaterMode.Heating, 25.0f);

        await bench.Host.ExecuteCodeAsync("M568 P1 A2");
        await bench.ReportHeaterAsync(1, HeaterMode.Heating, 25.0f);
        Assert.Multiple(async () =>
        {
            Assert.That(TemperatureMessages(bench), Is.EqualTo(new[] { ToolHeaterMessage(1, CanMessageSetHeaterTemperatureV1.CommandOn, 225.0f) }),
                        "only heater 1 is switched to tool 1's active temperature");
            Assert.That((await ReadHeaterAsync(bench, 0)).Active, Is.EqualTo(ToolActive), "heaters[0].active stays tool 0's");
        });

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync("M568 P1 A0");
        await bench.ReportHeaterAsync(1, HeaterMode.Off, 26.0f);
        Assert.That(TemperatureMessages(bench), Is.EqualTo(new[] { ToolHeaterMessage(1, CanMessageSetHeaterTemperatureV1.CommandOff, 225.0f) }),
                    "only heater 1 is switched off");

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync("M568 P1 S230:235");
        Assert.Multiple(async () =>
        {
            Assert.That((await ReadToolAsync(bench, 1)).Active, Is.EqualTo(new[] { 230.0f, 235.0f }), "tools[1].active");
            Assert.That((await ReadHeaterAsync(bench, 0)).Active, Is.EqualTo(ToolActive), "heaters[0].active stays tool 0's");
            Assert.That((await ReadHeaterAsync(bench, 0)).State, Is.EqualTo(HeaterState.Active), "heaters[0] stays active");
            Assert.That((await ReadHeaterAsync(bench, 1)).Active, Is.EqualTo(235.0f), "heaters[1].active");
            Assert.That(TemperatureMessages(bench), Is.Empty, "heater 0 belongs to tool 0 and heater 1 is off");
        });
    }

    /// <summary>
    /// A board refusing a new setpoint for an active heater fails M568, with the temperature already stored; a board
    /// that takes it with a warning does not fail it
    /// </summary>
    /// <remarks>
    /// RRF truth: Heater::SetTemperature stores the temperature, then throws a GCodeException when SwitchOn does not
    /// succeed; Tool::SetToolHeaterActiveOrStandbyTemperature stored the tool's temperature before calling it
    /// </remarks>
    [TestCase(CodeResult.Error, true)]
    [TestCase(CodeResult.Warning, false)]
    public async Task BoardAnswerToASetpointDecidesTheResult(CodeResult result, bool fails)
    {
        await using Bench bench = await Bench.StartAsync(HeaterToolWithTemperatures + "\nT0");
        await bench.ReportHeaterAsync(0, HeaterMode.Heating, 25.0f);
        bench.CanMaster.ReportWith<CanMessageSetHeaterTemperatureV1>("heater 0 cannot take that", result);

        string reply = await bench.Host.ExecuteCodeAsync("M568 S220");
        Assert.Multiple(async () =>
        {
            Assert.That(reply.StartsWith("Error"), Is.EqualTo(fails), "the board's refusal, and only a refusal, fails the code");
            Assert.That(TemperatureMessages(bench), Has.Length.EqualTo(1), "the setpoint was sent once");
            Assert.That((await ReadToolAsync(bench, 0)).Active[0], Is.EqualTo(220.0f), "tools[0].active[0] is stored either way");
            Assert.That((await ReadHeaterAsync(bench, 0)).Active, Is.EqualTo(220.0f), "heaters[0].active is stored either way");
        });
    }

    /// <summary>
    /// A board refusing to switch a heater on or off for A does not fail M568; the refusal is reported as an error
    /// message of its own
    /// </summary>
    /// <remarks>
    /// RRF truth: Tool::HeatersToActiveOrStandby reports a failed Heat::SetActiveOrStandby with Platform::MessageF, and
    /// RemoteHeater::SwitchOff reports "Failed to switch off remote heater", both without a result to return
    /// </remarks>
    [TestCase(1)]
    [TestCase(0)]
    public async Task BoardRefusingAHeaterStateIsReportedSeparately(int state)
    {
        const string refusal = "heater 0 output is shorted";
        await using Bench bench = await Bench.StartAsync(HeaterToolWithTemperatures + "\nM568 P0 A2");
        await bench.ReportHeaterAsync(0, HeaterMode.Heating, 25.0f);
        bench.CanMaster.ReportWith<CanMessageSetHeaterTemperatureV1>(refusal, CodeResult.Error);

        string reply = await bench.Host.ExecuteCodeAsync($"M568 P0 A{state}");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Reply.IsNotError, "M568 itself succeeds");
            Assert.That(TemperatureMessages(bench), Has.Length.EqualTo(1), "the heater state was sent once");
            Assert.That(await bench.Host.WaitForMessageAsync(MessageType.Error, refusal), Is.Not.Empty, "the board's refusal is reported as an error message");
        });
    }

    /// <summary>A heater in fault is not switched on by A2: nothing is sent, M568 succeeds and the refusal is reported</summary>
    /// <remarks>RRF truth: Heater::SetActiveOrStandby refuses a heater in fault mode, which Tool::HeatersToActiveOrStandby reports</remarks>
    [Test]
    public async Task FaultedHeaterIsNotSwitchedOn()
    {
        await using Bench bench = await Bench.StartAsync(HeaterToolWithTemperatures);
        bench.CanMaster.InjectHeatersStatus(srcAddress: Machines.DriverBoard, heaterNumber: 0, HeaterMode.Fault, currentTemperature: 25.0f);
        await bench.Host.WaitForModelAsync(model => model.Heat.Heaters[0]!.State, HeaterState.Fault, "heater 0's state");

        string reply = await bench.Host.ExecuteCodeAsync("M568 P0 A2");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Reply.IsNotError, "M568 itself succeeds");
            Assert.That(TemperatureMessages(bench), Is.Empty, "a heater in fault is not switched on");
            Assert.That(await bench.Host.WaitForMessageAsync(MessageType.Error, "fault"), Is.Not.Empty, "the refusal is reported as an error message");
            Assert.That((await ReadHeaterAsync(bench, 0)).State, Is.EqualTo(HeaterState.Fault), "heaters[0] stays in fault");
        });
    }

    /// <summary>
    /// F on a tool that is not current stores the RPM on the tool without touching its spindle, and the report shows
    /// it; F0 stops the spindle
    /// </summary>
    /// <remarks>
    /// RRF truth: Tool::SetSpindleRpm sets the spindle's configured RPM only for the current tool, and for 0 calls
    /// Spindle::SetState(stopped) and SetConfiguredRpm(0) on any tool; the report adds "spindle %d@%urpm".
    /// Regression: m568-spindle-rpm
    /// </remarks>
    [Test]
    public async Task SpindleRpmOnAToolNotSelectedIsKeptForTheTool()
    {
        await using Bench bench = await Bench.StartAsync(SpindleToolConfig);

        Assert.That(await bench.Host.ExecuteCodeAsync("M568 P0 F6000"), Reply.IsEmpty, "accepted");
        Spindle spindle = await ReadSpindleAsync(bench);
        Assert.Multiple(async () =>
        {
            Assert.That((await ReadToolAsync(bench, 0)).SpindleRpm, Is.EqualTo(6000), "tools[0].spindleRpm");
            Assert.That(spindle.Active, Is.EqualTo(0), "spindles[0].active is not the tool's until the tool is selected");
            Assert.That(spindle.State, Is.EqualTo(SpindleState.Stopped), "spindles[0] stays stopped");
            Assert.That(GpioWrites(bench), Is.Empty, "the spindle's port is not driven");
        });
        Assert.That(await bench.Host.ExecuteCodeAsync("M568 P0"), Reply.IsSuccess("6000"), "the report shows the tool's spindle RPM");

        Assert.That(await bench.Host.ExecuteCodeAsync("M568 P0 F0"), Reply.IsEmpty, "F0 is accepted");
        spindle = await ReadSpindleAsync(bench);
        Assert.Multiple(async () =>
        {
            Assert.That((await ReadToolAsync(bench, 0)).SpindleRpm, Is.EqualTo(0), "tools[0].spindleRpm");
            Assert.That(spindle.Active, Is.EqualTo(0), "spindles[0].active");
            Assert.That(spindle.State, Is.EqualTo(SpindleState.Stopped), "spindles[0].state");
        });
    }

    /// <summary>
    /// F on the current tool changes a running spindle's speed at once, and F0 stops it, one write to its PWM port
    /// each
    /// </summary>
    /// <remarks>
    /// RRF truth: Tool::SetSpindleRpm calls Spindle::SetConfiguredRpm(rpm, true) for the current tool, and for 0
    /// Spindle::SetState(stopped); Spindle::SetRpm writes the PWM port, the idle PWM when stopped
    /// </remarks>
    [TestCase(3000)]
    [TestCase(0)]
    public async Task SpindleRpmOnTheSelectedToolDrivesTheRunningSpindle(int rpm)
    {
        await using Bench bench = await Bench.StartAsync(SpindleToolConfig + "\nT0\nM3 S5000");
        Spindle spindle = await ReadSpindleAsync(bench);
        Assert.That(spindle.State, Is.EqualTo(SpindleState.Forward), "precondition: M3 in config.g ran the spindle");
        int min = spindle.Min ?? 0, max = spindle.Max ?? 0;

        Assert.That(await bench.Host.ExecuteCodeAsync($"M568 P0 F{rpm}"), Reply.IsEmpty, "accepted");
        spindle = await ReadSpindleAsync(bench);
        float pwm = (rpm == 0) ? 0.0f : MathF.Round((float)(rpm - min) / (max - min), 4);
        Assert.Multiple(async () =>
        {
            Assert.That((await ReadToolAsync(bench, 0)).SpindleRpm, Is.EqualTo(rpm), "tools[0].spindleRpm");
            Assert.That(spindle.Active, Is.EqualTo(rpm), "spindles[0].active");
            Assert.That(spindle.Current, Is.EqualTo(rpm), "spindles[0].current");
            Assert.That(spindle.State, Is.EqualTo((rpm == 0) ? SpindleState.Stopped : SpindleState.Forward), "spindles[0].state");
            Assert.That(GpioWrites(bench), Is.EqualTo(new[] { (Machines.DriverBoard, (byte)SpindleManager.PortNumberFor(0, 0), false, pwm) }),
                        "one write to the spindle's PWM port");
        });
    }

    /// <summary>An RPM above the spindle's range is refused, and nothing changes</summary>
    /// <remarks>RRF truth: Tool::SetSpindleRpm throws "Requested spindle RPM out of range" when !Spindle::IsValidRpm</remarks>
    [Test]
    public async Task SpindleRpmOutsideTheSpindlesRangeIsRefused()
    {
        await using Bench bench = await Bench.StartAsync(SpindleToolConfig);
        int max = (await ReadSpindleAsync(bench)).Max ?? 0;

        string reply = await bench.Host.ExecuteCodeAsync($"M568 P0 F{max + 1}");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Reply.IsError(), "refused");
            Assert.That((await ReadToolAsync(bench, 0)).SpindleRpm, Is.EqualTo(0), "tools[0].spindleRpm is unchanged");
            Assert.That((await ReadSpindleAsync(bench)).Active, Is.EqualTo(0), "spindles[0].active is unchanged");
            Assert.That(GpioWrites(bench), Is.Empty, "the spindle's port is not driven");
        });
    }

    /// <summary>
    /// S or R on a tool with no heaters is ignored, so M568 with nothing else reports the tool rather than setting
    /// anything
    /// </summary>
    /// <remarks>RRF truth: SetOrReportOffsets reads S and R only when HeaterCount() &gt; 0, and reports when nothing was set</remarks>
    [TestCase('S')]
    [TestCase('R')]
    public async Task TemperatureForAToolWithoutHeatersIsIgnoredAndTheToolReported(char letter)
    {
        await using Bench bench = await Bench.StartAsync("M563 P0 S\"Bare\"");

        string reply = (await bench.Host.ExecuteCodeAsync($"M568 P0 {letter}200")).Trim();
        Tool tool = await ReadToolAsync(bench, 0);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Tool 0"), "M568 reports the tool");
            Assert.That(tool.Active, Is.Empty, "tools[0].active stays empty");
            Assert.That(tool.Standby, Is.Empty, "tools[0].standby stays empty");
        });
    }

    /// <summary>
    /// F on a tool with no spindle is ignored, so M568 with nothing else reports the tool rather than setting anything
    /// </summary>
    /// <remarks>RRF truth: SetOrReportOffsets reads F only when GetSpindleNumber() &gt; -1, and reports when nothing was set</remarks>
    [Test]
    public async Task SpindleRpmForAToolWithoutASpindleIsIgnoredAndTheToolReported()
    {
        await using Bench bench = await Bench.StartAsync(HeaterToolWithTemperatures);

        string reply = (await bench.Host.ExecuteCodeAsync("M568 P0 F1000")).Trim();
        Tool tool = await ReadToolAsync(bench, 0);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Tool 0"), "M568 reports the tool");
            Assert.That(tool.SpindleRpm, Is.EqualTo(0), "tools[0].spindleRpm is unchanged");
            Assert.That(TemperatureMessages(bench), Is.Empty, "nothing is sent");
        });
    }
}
