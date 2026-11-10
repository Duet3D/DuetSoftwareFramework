using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Heat;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Codes;

/// <summary>
/// M140 and M141, the bed and chamber heaters, and M190 and M191, waiting for them, asserted against
/// RepRapFirmware's GCodes2.cpp case 140/141/190/191, Heat::SetBedHeaters/SetChamberHeaters/ClearBedHeaters/ClearChamberHeaters,
/// Heater::SetFunction, Heater::SetTemperature, Heater::SetActiveOrStandby and Heat::HeaterAtSetTemperature, and against
/// the regression suite's m140-*, m141-*, m190-* and m191-* references, which use heaters 3 and 4 on one expansion board
/// </summary>
[TestFixture]
public class BedChamberHeaterCodeTests : BenchFixture
{
    /// <summary>Heaters 3 and 4 on board 1 with no function, as the regression cases set them up, and tool 0 using heater 1</summary>
    private const string HeaterConfig = """
        M308 S1 P"1.temp2" Y"thermistor" T100000 B4138
        M950 H1 C"1.out2" T1
        M563 P0 H1
        M308 S3 P"1.temp0" Y"thermistor" T100000 B4138
        M308 S4 P"1.temp1" Y"thermistor" T100000 B4138
        M950 H3 C"1.out0" T3
        M950 H4 C"1.out1" T4
        """;

    private static async Task<JobBench> StartAsync(string extraConfig = "")
    {
        JobBench bench = await JobControlBench.StartAsync(configExtra: HeaterConfig + "\n" + extraConfig);
        bench.CanMaster.ClearCapture();
        return bench;
    }

    private static Task<Heater> ReadHeaterAsync(JobBench bench, int heater)
        => bench.Host.ReadModelAsync(model => model.Heat.Heaters[heater]!);

    private static Task<int[][]> ReadMappingAsync(JobBench bench, bool chamber)
        => bench.Host.ReadModelAsync(model => (chamber ? model.Heat.ChamberHeaterMapping : model.Heat.BedHeaterMapping)
                                              .Select(slot => slot.ToArray()).ToArray());

#pragma warning disable CS0618
    private static Task<int[]> ReadObsoleteHeatersAsync(JobBench bench, bool chamber)
        => bench.Host.ReadModelAsync(model => (chamber ? model.Heat.ChamberHeaters : model.Heat.BedHeaters).ToArray());
#pragma warning restore CS0618

    private static CanMessageSetHeaterTemperatureV1[] TemperatureMessages(JobBench bench)
        => bench.CanMaster.CanMessages<CanMessageSetHeaterTemperatureV1>().Select(m => m.Message).ToArray();

    /// <summary>The code that assigns the heaters of the slots M190 (M140) or M191 (M141) waits for</summary>
    private static string SetCode(string waitCode) => waitCode == "M190" ? "M140" : "M141";

    /// <summary>Heater 3 in slot 0 and heater 4 in slot 1 of the bed (M140) or chamber (M141)</summary>
    private static string TwoSlotsConfig(string setCode) => $"{setCode} H3\n{setCode} P1 H4";

    private static async Task ReportTemperatureAsync(JobBench bench, int heater, HeaterMode mode, float temperature)
    {
        bench.CanMaster.InjectHeatersStatus(srcAddress: 1, heaterNumber: heater, mode, currentTemperature: temperature);
        await bench.CanMaster.WaitUntilAsync(() => bench.Host.Model.Heat.Heaters[heater]?.Current is float t && System.MathF.Abs(t - temperature) < 0.01f,
                                             what: $"heater {heater} reporting {temperature}C");
    }

    /// <summary>
    /// M140 H assigns a heater to bed slot 0, asks its board for the default bed model, and sets the default bed
    /// over-temperature monitor
    /// </summary>
    /// <remarks>
    /// RRF truth: Heat::SetBedHeaters, Heater::SetFunction, RemoteHeater::SetDefaultModel and FopDt::SetDefaultModel,
    /// which calculates the PID constants at the model's typical temperature. Regression: m140-configure-bed-heater
    /// </remarks>
    [Test]
    public async Task AssigningBedHeaterSetsDefaultModelAndMonitor()
    {
        await using JobBench bench = await StartAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync("M140 H3")).Trim(), Is.Empty, "assigning a bed heater replies nothing");

        (byte board, CanMessageSetDefaultHeaterModel request) = bench.CanMaster.LastCanMessage<CanMessageSetDefaultHeaterModel>();
        Heater heater = await ReadHeaterAsync(bench, 3);
        Assert.Multiple(async () =>
        {
            Assert.That(board, Is.EqualTo(1), "the request goes to the heater's board");
            Assert.That(request.Heater, Is.EqualTo(3), "setDefaultHeaterModel.heater");
            Assert.That(request.HeaterFunction, Is.EqualTo(HeaterFunction.Bed), "setDefaultHeaterModel.heaterFunction");
            Assert.That(TemperatureMessages(bench), Is.Empty, "a slot with no heaters has nothing to switch off");

            Assert.That(await ReadMappingAsync(bench, chamber: false), Is.EqualTo(new[] { new[] { 3 } }), "heat.bedHeaterMapping");
            int[] obsolete = await ReadObsoleteHeatersAsync(bench, chamber: false);
            Assert.That(obsolete, Has.Length.EqualTo(12), "heat.bedHeaters has MaxBedHeaters entries");
            Assert.That(obsolete, Is.EqualTo(new[] { 3 }.Concat(Enumerable.Repeat(-1, 11))), "heat.bedHeaters is the first heater of each slot");

            Assert.That(heater.Model.Enabled, Is.True, "model.enabled");
            Assert.That(heater.Model.HeatingRate, Is.EqualTo(0.13).Within(1e-4), "model.heatingRate");
            Assert.That(heater.Model.CoolingRate, Is.EqualTo(0.15).Within(1e-4), "model.coolingRate");
            Assert.That(heater.Model.DeadTime, Is.EqualTo(10.0).Within(1e-4), "model.deadTime");
            Assert.That(heater.Model.CoolingExp, Is.EqualTo(1.35).Within(1e-4), "model.coolingExp");
            Assert.That(heater.Model.PID.Used, Is.False, "model.pid.used");
            Assert.That(heater.Model.PID.P, Is.EqualTo(0.53846).Within(1e-5), "model.pid.p at 60C");
            Assert.That(heater.Model.PID.I, Is.EqualTo(0.01508).Within(1e-5), "model.pid.i at 60C");
            Assert.That(heater.Model.PID.D, Is.EqualTo(3.769).Within(1e-3), "model.pid.d at 60C");

            Assert.That(heater.Monitors, Has.Count.EqualTo(3), "a heater has MaxMonitorsPerHeater monitors");
            Assert.That(heater.Monitors[0]!.Sensor, Is.EqualTo(3), "monitors[0].sensor is the heater's sensor");
            Assert.That(heater.Monitors[0]!.Condition, Is.EqualTo(HeaterMonitorCondition.TooHigh), "monitors[0].condition");
            Assert.That(heater.Monitors[0]!.Limit, Is.EqualTo(125.0f), "monitors[0].limit is DefaultBedTemperatureLimit");
            Assert.That(heater.Monitors[0]!.Action, Is.EqualTo(HeaterMonitorAction.GenerateFault), "monitors[0].action");
            Assert.That(heater.Max, Is.EqualTo(125.0f), "max follows the monitor");
        });
    }

    /// <summary>M141 H does the same for a chamber, with the chamber defaults</summary>
    /// <remarks>RRF truth: Heat::SetChamberHeaters. Regression: m141-configure-chamber-heater</remarks>
    [Test]
    public async Task AssigningChamberHeaterSetsDefaultModelAndMonitor()
    {
        await using JobBench bench = await StartAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync("M141 H3")).Trim(), Is.Empty, "assigning a chamber heater replies nothing");

        CanMessageSetDefaultHeaterModel request = bench.CanMaster.LastCanMessage<CanMessageSetDefaultHeaterModel>().Message;
        Heater heater = await ReadHeaterAsync(bench, 3);
        Assert.Multiple(async () =>
        {
            Assert.That(request.HeaterFunction, Is.EqualTo(HeaterFunction.Chamber), "setDefaultHeaterModel.heaterFunction");
            Assert.That(await ReadMappingAsync(bench, chamber: true), Is.EqualTo(new[] { new[] { 3 } }), "heat.chamberHeaterMapping");
            int[] obsolete = await ReadObsoleteHeatersAsync(bench, chamber: true);
            Assert.That(obsolete, Is.EqualTo(new[] { 3 }.Concat(Enumerable.Repeat(-1, 7))), "heat.chamberHeaters has MaxChamberHeaters entries");
            Assert.That(heater.Model.DeadTime, Is.EqualTo(30.0).Within(1e-4), "model.deadTime");
            Assert.That(heater.Model.PID.P, Is.EqualTo(0.17949).Within(1e-5), "model.pid.p at 60C");
            Assert.That(heater.Model.PID.I, Is.EqualTo(0.002205).Within(1e-6), "model.pid.i at 60C");
            Assert.That(heater.Monitors[0]!.Limit, Is.EqualTo(100.0f), "monitors[0].limit is DefaultChamberTemperatureLimit");
            Assert.That(heater.Max, Is.EqualTo(100.0f), "max follows the monitor");
        });
    }

    /// <summary>
    /// Reassigning a slot switches its old heaters off before the new ones get their function, a slot may hold
    /// several heaters and a heater may be in several bed slots, and H-1 switches a slot's heaters off and empties it
    /// </summary>
    /// <remarks>RRF truth: Heat::SetBedHeaters and Heat::ClearBedHeaters. Regression: m140-configure-bed-heater</remarks>
    [Test]
    public async Task ReassigningAndClearingSlots()
    {
        await using JobBench bench = await StartAsync();

        await bench.Host.ExecuteCodeAsync("M140 H3");
        await bench.Host.ExecuteCodeAsync("M140 P1 H4");
        bench.CanMaster.ClearCapture();

        Assert.That((await bench.Host.ExecuteCodeAsync("M140 H3:4")).Trim(), Is.Empty, "M140 H3:4 replies nothing");
        CanMessageSetHeaterTemperatureV1[] off = TemperatureMessages(bench);
        CanMessageSetDefaultHeaterModel[] defaults = bench.CanMaster.CanMessages<CanMessageSetDefaultHeaterModel>().Select(m => m.Message).ToArray();
        Assert.Multiple(async () =>
        {
            Assert.That(off.Select(m => (m.HeaterNumber, m.Command, m.Function, m.SetPoint)),
                        Is.EqualTo(new[] { ((byte)3, CanMessageSetHeaterTemperatureV1.CommandOff, (byte)HeaterFunction.Bed, 0.0f) }),
                        "the slot's old heater is switched off");
            Assert.That(defaults.Select(m => m.Heater), Is.EqualTo(new byte[] { 3, 4 }), "both new heaters get the default bed model");
            Assert.That(await ReadMappingAsync(bench, chamber: false), Is.EqualTo(new[] { new[] { 3, 4 }, new[] { 4 } }), "heat.bedHeaterMapping");
        });

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync("M140 P1 H-1");
        Assert.Multiple(async () =>
        {
            Assert.That(TemperatureMessages(bench).Select(m => (m.HeaterNumber, m.Command)),
                        Is.EqualTo(new[] { ((byte)4, CanMessageSetHeaterTemperatureV1.CommandOff) }), "H-1 switches slot 1's heater off");
            Assert.That(await ReadMappingAsync(bench, chamber: false), Is.EqualTo(new[] { new[] { 3, 4 } }), "the empty last slot is not reported");
        });

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync("M140 H-1");
        Heater heater = await ReadHeaterAsync(bench, 3);
        Assert.Multiple(async () =>
        {
            Assert.That(TemperatureMessages(bench).Select(m => (m.HeaterNumber, m.Command)),
                        Is.EqualTo(new[] { ((byte)3, CanMessageSetHeaterTemperatureV1.CommandOff), ((byte)4, CanMessageSetHeaterTemperatureV1.CommandOff) }),
                        "H-1 switches both of slot 0's heaters off");
            Assert.That(await ReadMappingAsync(bench, chamber: false), Is.Empty, "heat.bedHeaterMapping");
            Assert.That(await ReadObsoleteHeatersAsync(bench, chamber: false), Is.EqualTo(Enumerable.Repeat(-1, 12)), "heat.bedHeaters");
            Assert.That(heater.Model.Enabled, Is.True, "clearing a slot keeps the heater's model");
            Assert.That(heater.Max, Is.EqualTo(125.0f), "clearing a slot keeps the heater's monitor");
        });
    }

    /// <summary>A model set by M307 and monitors set by M143 are kept when the heater is given a function</summary>
    /// <remarks>RRF truth: Heater::SetFunction tests modelSetByUser and monitorsSetByUser</remarks>
    [TestCase("M140")]
    [TestCase("M141")]
    public async Task UserModelAndMonitorsAreKept(string code)
    {
        await using JobBench bench = await StartAsync();

        await bench.Host.ExecuteCodeAsync("M307 H3 R2.5 K0.35 D5.5 B0");
        await bench.Host.ExecuteCodeAsync("M143 H3 S200");
        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync($"{code} H3");

        Heater heater = await ReadHeaterAsync(bench, 3);
        Assert.Multiple(() =>
        {
            Assert.That(bench.CanMaster.CanMessages<CanMessageSetDefaultHeaterModel>(), Is.Empty, "no default model is asked for");
            Assert.That(heater.Model.HeatingRate, Is.EqualTo(2.5).Within(1e-4), "the M307 model stays");
            Assert.That(heater.Monitors[0]!.Limit, Is.EqualTo(200.0f), "the M143 monitor stays");
            Assert.That(heater.Max, Is.EqualTo(200.0f), "max stays");
        });
    }

    /// <summary>
    /// H refuses a heater number out of range, a heater a tool uses, a heater that is the other kind of slot's, a
    /// negative number in a list and too many heaters, and changes nothing when it does. A heater number in range
    /// that has no heater is accepted
    /// </summary>
    /// <remarks>RRF truth: GCodes2.cpp case 140/141</remarks>
    [TestCase("M140 H40", "Invalid heater number '40'")]
    [TestCase("M140 H-1:3", "Invalid heater number '-1'")]
    [TestCase("M140 H1", "Heater 1 is already assigned to a tool")]
    [TestCase("M141 H4", "Heater 4 is already assigned as a bed heater")]
    [TestCase("M140 H3:4:5:6:7", "")]
    [TestCase("M140 P12 H3", "")]
    [TestCase("M141 H40", "Invalid heater number '40'")]
    [TestCase("M141 H-1:3", "Invalid heater number '-1'")]
    [TestCase("M141 H1", "Heater 1 is already assigned to a tool")]
    [TestCase("M141 H3:5:6:7:8", "")]
    [TestCase("M141 P8 H3", "")]
    public async Task RefusesBadAssignment(string code, string error)
    {
        await using JobBench bench = await StartAsync("M140 P1 H4");

        string reply = await bench.Host.ExecuteCodeAsync(code);
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain(error), "the assignment is refused");
            Assert.That(await ReadMappingAsync(bench, chamber: false), Is.EqualTo(new[] { System.Array.Empty<int>(), new[] { 4 } }), "heat.bedHeaterMapping is unchanged");
            Assert.That(await ReadMappingAsync(bench, chamber: true), Is.Empty, "heat.chamberHeaterMapping is unchanged");
        });
    }

    /// <summary>A heater number in range that has no heater is accepted into the slot, and gets no function</summary>
    [TestCase("M140", false)]
    [TestCase("M141", true)]
    public async Task AcceptsUndefinedHeater(string code, bool chamber)
    {
        await using JobBench bench = await StartAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync($"{code} H7")).Trim(), Is.Empty, "an undefined heater is accepted");
        Assert.Multiple(async () =>
        {
            Assert.That(await ReadMappingAsync(bench, chamber), Is.EqualTo(new[] { new[] { 7 } }), "the slot mapping");
            Assert.That(bench.CanMaster.CanMessages<CanMessageSetDefaultHeaterModel>(), Is.Empty, "nothing is sent for it");
            Assert.That(await bench.Host.ExecuteCodeAsync($"{code} S50"), Does.StartWith("Error").And.Contain("Heater 7 not found"),
                        "switching it on is refused (RRF Heat::SetActiveOrStandby)");
        });
    }

    /// <summary>S sets the active temperature and switches the heater on, sending the bed function</summary>
    /// <remarks>RRF truth: Heater::SetTemperature and RemoteHeater::SwitchOn. Regression: m140-set-bed-temperature, m141-set-chamber-temperature</remarks>
    [TestCase("M140", HeaterFunction.Bed)]
    [TestCase("M141", HeaterFunction.Chamber)]
    public async Task SetsActiveTemperatureAndSwitchesOn(string code, HeaterFunction function)
    {
        await using JobBench bench = await StartAsync($"{code} H3");

        Assert.That((await bench.Host.ExecuteCodeAsync($"{code} S35")).Trim(), Is.Empty, "S replies nothing");
        Heater heater = await ReadHeaterAsync(bench, 3);
        Assert.Multiple(() =>
        {
            Assert.That(TemperatureMessages(bench).Select(m => (m.HeaterNumber, m.Command, m.Function, m.SetPoint)),
                        Is.EqualTo(new[] { ((byte)3, CanMessageSetHeaterTemperatureV1.CommandOn, (byte)function, 35.0f) }),
                        "one setHeaterTemperature switches the heater on at the active temperature");
            Assert.That(heater.Active, Is.EqualTo(35.0f), "active");
            Assert.That(heater.State, Is.EqualTo(HeaterState.Active), "state");
        });
    }

    /// <summary>S with R sets both, switching the heater on once at the active temperature</summary>
    /// <remarks>Regression: m140-bed-standby</remarks>
    [TestCase("M140")]
    [TestCase("M141")]
    public async Task ActiveAndStandbyTogether(string code)
    {
        await using JobBench bench = await StartAsync($"{code} H3");

        await bench.Host.ExecuteCodeAsync($"{code} S35 R30");
        Heater heater = await ReadHeaterAsync(bench, 3);
        Assert.Multiple(() =>
        {
            Assert.That(TemperatureMessages(bench).Select(m => (m.Command, m.SetPoint)),
                        Is.EqualTo(new[] { (CanMessageSetHeaterTemperatureV1.CommandOn, 35.0f) }), "one message, at the active temperature");
            Assert.That(heater.Active, Is.EqualTo(35.0f), "active");
            Assert.That(heater.Standby, Is.EqualTo(30.0f), "standby");
            Assert.That(heater.State, Is.EqualTo(HeaterState.Active), "state");
        });
    }

    /// <summary>R alone stores the standby temperature without switching an off heater on</summary>
    /// <remarks>RRF truth: Heater::SetTemperature only resends when the heater is on in the matching mode</remarks>
    [TestCase("M140")]
    [TestCase("M141")]
    public async Task StandbyAloneDoesNotSwitchOn(string code)
    {
        await using JobBench bench = await StartAsync($"{code} H3");

        Assert.That((await bench.Host.ExecuteCodeAsync($"{code} R40")).Trim(), Is.Empty, "R replies nothing");
        Heater heater = await ReadHeaterAsync(bench, 3);
        Assert.Multiple(() =>
        {
            Assert.That(TemperatureMessages(bench), Is.Empty, "nothing is sent");
            Assert.That(heater.Standby, Is.EqualTo(40.0f), "standby");
            Assert.That(heater.State, Is.EqualTo(HeaterState.Off), "state");
        });
    }

    /// <summary>
    /// S on a heater that is already active resends the setpoint and then switches it on again, two messages, and
    /// recalculates the PID constants at the new temperature
    /// </summary>
    /// <remarks>RRF truth: Heater::SetTemperature (SwitchOn, CalcPidConstants(activeTemperature)) then Heater::SetActiveOrStandby</remarks>
    [TestCase("M140", 0.01612)]
    [TestCase("M141", 0.002357)]
    public async Task ActiveHeaterIsUpdatedTwice(string code, double integral)
    {
        await using JobBench bench = await StartAsync($"{code} H3\n{code} S35");
        bench.CanMaster.ClearCapture();

        await bench.Host.ExecuteCodeAsync($"{code} S100");
        Heater heater = await ReadHeaterAsync(bench, 3);
        Assert.Multiple(() =>
        {
            Assert.That(TemperatureMessages(bench).Select(m => (m.Command, m.SetPoint)),
                        Is.EqualTo(new[] { (CanMessageSetHeaterTemperatureV1.CommandOn, 100.0f), (CanMessageSetHeaterTemperatureV1.CommandOn, 100.0f) }),
                        "the setpoint goes out from SetTemperature and again from SetActiveOrStandby");
            Assert.That(heater.Model.PID.I, Is.EqualTo(integral).Within(1e-6), "model.pid.i is recalculated at 100C, not the 60C default");
        });
    }

    /// <summary>S0 is a temperature like any other: it sets 0 and switches the heater on</summary>
    /// <remarks>RRF truth: only S <= NEARLY_ABS_ZERO (-273) switches off</remarks>
    [TestCase("M140")]
    [TestCase("M141")]
    public async Task ZeroSwitchesOnAtZero(string code)
    {
        await using JobBench bench = await StartAsync($"{code} H3");

        await bench.Host.ExecuteCodeAsync($"{code} S0");
        Heater heater = await ReadHeaterAsync(bench, 3);
        Assert.Multiple(() =>
        {
            Assert.That(TemperatureMessages(bench).Select(m => (m.Command, m.SetPoint)),
                        Is.EqualTo(new[] { (CanMessageSetHeaterTemperatureV1.CommandOn, 0.0f) }), "the heater is switched on at 0");
            Assert.That(heater.State, Is.EqualTo(HeaterState.Active), "state");
        });
    }

    /// <summary>S at or below -273 switches the heater off and leaves its active temperature alone</summary>
    /// <remarks>RRF truth: Heat::SwitchOff and RemoteHeater::SwitchOff, which sends the target temperature</remarks>
    [TestCase("M140", HeaterFunction.Bed)]
    [TestCase("M141", HeaterFunction.Chamber)]
    public async Task BelowAbsoluteZeroSwitchesOff(string code, HeaterFunction function)
    {
        await using JobBench bench = await StartAsync($"{code} H3\n{code} S35");
        bench.CanMaster.ClearCapture();

        Assert.That((await bench.Host.ExecuteCodeAsync($"{code} S-273.1")).Trim(), Is.Empty, "switching off replies nothing");
        Heater heater = await ReadHeaterAsync(bench, 3);
        Assert.Multiple(() =>
        {
            Assert.That(TemperatureMessages(bench).Select(m => (m.Command, m.Function, m.SetPoint)),
                        Is.EqualTo(new[] { (CanMessageSetHeaterTemperatureV1.CommandOff, (byte)function, 35.0f) }),
                        "the heater is switched off");
            Assert.That(heater.Active, Is.EqualTo(35.0f), "active is unchanged");
            Assert.That(heater.State, Is.EqualTo(HeaterState.Off), "state");
        });
    }

    /// <summary>A temperature outside the heater's monitored limits is refused before anything changes</summary>
    /// <remarks>RRF truth: Heater::SetTemperature</remarks>
    [TestCase("M140 S130", "Temperature too high for heater 3")]
    [TestCase("M140 R130", "Temperature too high for heater 3")]
    [TestCase("M140 S-10", "Temperature too low for heater 3")]
    [TestCase("M141 S130", "Temperature too high for heater 3")]
    [TestCase("M141 R130", "Temperature too high for heater 3")]
    [TestCase("M141 S-10", "Temperature too low for heater 3")]
    public async Task RefusesTemperatureOutsideLimits(string code, string error)
    {
        await using JobBench bench = await StartAsync($"{code[..4]} H3\nM143 H3 P1 S0 C1");
        bench.CanMaster.ClearCapture();

        string reply = await bench.Host.ExecuteCodeAsync(code);
        Heater heater = await ReadHeaterAsync(bench, 3);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain(error), "the temperature is refused");
            Assert.That(TemperatureMessages(bench), Is.Empty, "nothing is sent");
            Assert.That(heater.Active, Is.EqualTo(0.0f), "active is unchanged");
            Assert.That(heater.Standby, Is.EqualTo(0.0f), "standby is unchanged");
            Assert.That(heater.Min, Is.EqualTo(0.0f), "min follows the too-low monitor");
        });
    }

    /// <summary>A heater in a fault is not switched on, though its active temperature is set</summary>
    /// <remarks>RRF truth: Heater::SetActiveOrStandby</remarks>
    [TestCase("M140")]
    [TestCase("M141")]
    public async Task RefusesToSwitchOnFaultedHeater(string code)
    {
        await using JobBench bench = await StartAsync($"{code} H3");
        bench.CanMaster.InjectHeatersStatus(srcAddress: 1, heaterNumber: 3, HeaterMode.Fault, currentTemperature: 25.0f);
        await bench.CanMaster.WaitUntilAsync(() => bench.Host.Model.Heat.Heaters[3]?.State == HeaterState.Fault, what: "heater 3 faulting");

        string reply = await bench.Host.ExecuteCodeAsync($"{code} S50");
        Heater heater = await ReadHeaterAsync(bench, 3);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain("Can't turn heater 3 on while in fault state"), "switching on is refused");
            Assert.That(TemperatureMessages(bench), Is.Empty, "nothing is sent");
            Assert.That(heater.Active, Is.EqualTo(50.0f), "active is still set");
        });
    }

    /// <summary>Every heater of a slot is set</summary>
    [TestCase("M140")]
    [TestCase("M141")]
    public async Task SetsEveryHeaterOfSlot(string code)
    {
        await using JobBench bench = await StartAsync($"{code} H3:4");

        await bench.Host.ExecuteCodeAsync($"{code} S50");
        Assert.That(TemperatureMessages(bench).Select(m => (m.HeaterNumber, m.Command, m.SetPoint)),
                    Is.EqualTo(new[] { ((byte)3, CanMessageSetHeaterTemperatureV1.CommandOn, 50.0f), ((byte)4, CanMessageSetHeaterTemperatureV1.CommandOn, 50.0f) }),
                    "both heaters are switched on");
    }

    /// <summary>
    /// With no heater in the slot, S above zero and R are refused, and S at or below zero is accepted because turning
    /// off a heater that does not exist is not an error
    /// </summary>
    /// <remarks>RRF truth: GCodes2.cpp case 140/141</remarks>
    [TestCase("M140 S50", "No bed heater has been configured for slot 0")]
    [TestCase("M140 R40", "No bed heater has been configured for slot 0")]
    [TestCase("M141 P2 S10", "No chamber heater has been configured for slot 2")]
    [TestCase("M140 S0", null)]
    [TestCase("M140 S-273.1", null)]
    [TestCase("M141 S50", "No chamber heater has been configured for slot 0")]
    [TestCase("M141 R40", "No chamber heater has been configured for slot 0")]
    [TestCase("M141 S0", null)]
    [TestCase("M141 S-273.1", null)]
    public async Task EmptySlot(string code, string? error)
    {
        await using JobBench bench = await StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync(code);
        if (error is null)
        {
            Assert.That(reply.Trim(), Is.Empty, "accepted");
        }
        else
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain(error), "refused");
        }
    }

    /// <summary>With neither H, S nor R, the slot's heaters are reported with their temperatures</summary>
    /// <remarks>RRF truth: GCodes2.cpp case 140/141; Heat::GetHeaterTemperature is ABS_ZERO for a heater that does not exist</remarks>
    [Test]
    public async Task ReportsSlot()
    {
        await using JobBench bench = await StartAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync("M140")).Trim(), Is.EqualTo("No bed heater has been configured for slot 0"), "an empty slot");

        await bench.Host.ExecuteCodeAsync("M141 P1 H3:7");
        bench.CanMaster.InjectHeatersStatus(srcAddress: 1, heaterNumber: 3, HeaterMode.Off, currentTemperature: 21.5f);
        await bench.CanMaster.WaitUntilAsync(() => bench.Host.Model.Heat.Heaters[3]?.Current is > 21.0f, what: "heater 3's temperature");
        Assert.That((await bench.Host.ExecuteCodeAsync("M141 P1")).Trim(), Is.EqualTo("Chamber heaters for slot 1: 3 (21.5°C) 7 (-273.1°C)"),
                    "the slot's heaters");
    }

    /// <summary>
    /// M190 S switches the slot's heater on once; R on a heater that is on resends and switches it on again, and
    /// recalculates the PID constants. A target below 40C is not waited for
    /// </summary>
    /// <remarks>Regression: m190-wait-for-bed-temperature, m190-bed-heater-slot, m191-wait-for-chamber-temperature, m191-chamber-heater-slot</remarks>
    [TestCase("M190", "", 3, HeaterFunction.Bed, 0.011048)]
    [TestCase("M190", " P1", 4, HeaterFunction.Bed, 0.011048)]
    [TestCase("M191", "", 3, HeaterFunction.Chamber, 0.001616)]
    [TestCase("M191", " P1", 4, HeaterFunction.Chamber, 0.001616)]
    public async Task WaitSetsAndSwitchesOn(string code, string slot, int heaterNumber, HeaterFunction function, double integral)
    {
        await using JobBench bench = await StartAsync(TwoSlotsConfig(SetCode(code)));

        Assert.That((await bench.Host.ExecuteCodeAsync($"{code}{slot} S0")).Trim(), Is.Empty, "S replies nothing");
        Assert.That((await bench.Host.ExecuteCodeAsync($"{code}{slot} R0")).Trim(), Is.Empty, "R replies nothing");
        Heater heater = await ReadHeaterAsync(bench, heaterNumber);
        Assert.Multiple(() =>
        {
            Assert.That(TemperatureMessages(bench).Select(m => (m.HeaterNumber, m.Command, m.Function, m.SetPoint)),
                        Is.EqualTo(Enumerable.Repeat(((byte)heaterNumber, CanMessageSetHeaterTemperatureV1.CommandOn, (byte)function, 0.0f), 3)),
                        "three messages at 0C");
            Assert.That(heater.State, Is.EqualTo(HeaterState.Active), "state");
            Assert.That(heater.Model.PID.I, Is.EqualTo(integral).Within(1e-6), "model.pid.i is recalculated at 0C (regression)");
        });
    }

    /// <summary>M190 and M191 with no heater in the slot, or with neither S nor R, do nothing</summary>
    [TestCase("M190 P2 S50", "M140")]
    [TestCase("M190", "M140")]
    [TestCase("M191 S50", "M140")]
    [TestCase("M191 P2 S50", "M141")]
    [TestCase("M191", "M141")]
    [TestCase("M190 S50", "M141")]
    public async Task WaitWithNothingToDo(string code, string setCode)
    {
        await using JobBench bench = await StartAsync(TwoSlotsConfig(setCode));

        Assert.That((await bench.Host.ExecuteCodeAsync(code)).Trim(), Is.Empty, "accepted");
        Assert.That(TemperatureMessages(bench), Is.Empty, "nothing is sent");
    }

    /// <summary>M190 and M191 R wait for the slot to cool to the target, and S does not</summary>
    /// <remarks>RRF truth: GCodes2.cpp case 190/191, Heat::HeaterAtSetTemperature</remarks>
    [TestCase("M190")]
    [TestCase("M191")]
    public async Task WaitsWhileCoolingOnlyForR(string code)
    {
        await using JobBench bench = await StartAsync(TwoSlotsConfig(SetCode(code)));
        HeatManager heat = bench.Host.Services.GetRequiredService<HeatManager>();
        await ReportTemperatureAsync(bench, 3, HeaterMode.Stable, 80.0f);

        Assert.That((await bench.Host.ExecuteCodeAsync($"{code} S60")).Trim(), Is.Empty, "a heater above an S target is not waited for");

        Task<string> waiting = bench.Host.ExecuteCodeAsync($"{code} R60");
        await bench.CanMaster.WaitUntilAsync(() => heat.IsWaitingForTemperatures, what: $"{code} R waiting");
        Assert.That(waiting.IsCompleted, Is.False, "a heater above an R target is waited for");
        await ReportTemperatureAsync(bench, 3, HeaterMode.Stable, 61.0f);
        Assert.That((await waiting).Trim(), Is.Empty, "within 1.5C ends the wait");
    }

    /// <summary>M190 and M191 refuse a faulted heater, and from a channel that is not a file do not wait for it</summary>
    /// <remarks>RRF truth: Heater::SetActiveOrStandby, and HeaterAtSetTemperature waits on a fault only for a file channel</remarks>
    [TestCase("M190")]
    [TestCase("M191")]
    public async Task FaultedHeaterIsNotWaitedForOutsideAFile(string code)
    {
        await using JobBench bench = await StartAsync(TwoSlotsConfig(SetCode(code)));
        await ReportTemperatureAsync(bench, 3, HeaterMode.Fault, 25.0f);

        string reply = await bench.Host.ExecuteCodeAsync($"{code} S60");
        Assert.That(reply, Does.StartWith("Error").And.Contain("Can't turn heater 3 on while in fault state"), "the fault is reported");
    }

    /// <summary>
    /// M190 and M191 set and switch on every heater of the slot before any is waited for, and wait until all of them
    /// are at the target
    /// </summary>
    /// <remarks>RRF truth: GCodes2.cpp case 190/191 sets every heater of the slot, then waits for each in turn</remarks>
    [TestCase("M190", HeaterFunction.Bed)]
    [TestCase("M191", HeaterFunction.Chamber)]
    public async Task WaitsForEveryHeaterOfSlot(string code, HeaterFunction function)
    {
        await using JobBench bench = await StartAsync($"{SetCode(code)} H3:4");
        HeatManager heat = bench.Host.Services.GetRequiredService<HeatManager>();
        await ReportTemperatureAsync(bench, 3, HeaterMode.Off, 25.0f);
        await ReportTemperatureAsync(bench, 4, HeaterMode.Off, 25.0f);

        Task<string> waiting = bench.Host.ExecuteCodeAsync($"{code} S50");
        await bench.CanMaster.WaitUntilAsync(() => heat.IsWaitingForTemperatures, what: $"{code} waiting");
        Assert.That(TemperatureMessages(bench).Select(m => (m.HeaterNumber, m.Command, m.Function, m.SetPoint)),
                    Is.EqualTo(new[] { ((byte)3, CanMessageSetHeaterTemperatureV1.CommandOn, (byte)function, 50.0f),
                                       ((byte)4, CanMessageSetHeaterTemperatureV1.CommandOn, (byte)function, 50.0f) }),
                    "both heaters are switched on before the wait");

        await ReportTemperatureAsync(bench, 3, HeaterMode.Stable, 50.0f);
        Assert.That(waiting.IsCompleted, Is.False, "heater 4 is still cold");
        await ReportTemperatureAsync(bench, 4, HeaterMode.Stable, 50.0f);
        Assert.That((await waiting).Trim(), Is.Empty, "both at the target ends the wait");
    }

    /// <summary>M190 and M191 refuse a temperature above the heater's limit, send nothing and wait for nothing</summary>
    /// <remarks>RRF truth: Heater::SetTemperature throws; the default limits are 125C for a bed and 100C for a chamber</remarks>
    [TestCase("M190")]
    [TestCase("M191")]
    public async Task WaitRefusesTemperatureAboveLimit(string code)
    {
        await using JobBench bench = await StartAsync(TwoSlotsConfig(SetCode(code)));

        string reply = await bench.Host.ExecuteCodeAsync($"{code} S130");
        Heater heater = await ReadHeaterAsync(bench, 3);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain("Temperature too high for heater 3"), "the temperature is refused");
            Assert.That(TemperatureMessages(bench), Is.Empty, "nothing is sent");
            Assert.That(heater.Active, Is.EqualTo(0.0f), "active is unchanged");
            Assert.That(heater.State, Is.EqualTo(HeaterState.Off), "state");
        });
    }

    /// <summary>M190 and M191 refuse a slot number past the last bed or chamber slot</summary>
    /// <remarks>RRF truth: GetLimitedUIValue('P', MaxBedHeaters or MaxChamberHeaters)</remarks>
    [TestCase("M190", 12)]
    [TestCase("M191", 8)]
    public async Task WaitRefusesSlotOutOfRange(string code, int slot)
    {
        await using JobBench bench = await StartAsync(TwoSlotsConfig(SetCode(code)));

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ExecuteCodeAsync($"{code} P{slot} S50"), Does.StartWith("Error"), "the slot is refused");
            Assert.That(TemperatureMessages(bench), Is.Empty, "nothing is sent");
        });
    }
}
