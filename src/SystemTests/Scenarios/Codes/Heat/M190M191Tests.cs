using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Heat;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Heat.BedChamberSetup;

namespace SystemTests.Scenarios.Codes.Heat;

/// <summary>
/// M190 and M191, waiting for the bed and chamber heaters, asserted against RepRapFirmware's GCodes2.cpp case
/// 190/191, Heater::SetActiveOrStandby and Heat::HeaterAtSetTemperature, and against the regression suite's
/// m190-* and m191-* references, which use heaters 3 and 4 on one expansion board
/// </summary>
[TestFixture]
[GCode("M190", "M191")]
public class M190M191Tests : BenchFixture
{
    /// <summary>The code that assigns the heaters of the slots M190 (M140) or M191 (M141) waits for</summary>
    private static string SetCode(string waitCode) => waitCode == "M190" ? "M140" : "M141";

    /// <summary>Heater 3 in slot 0 and heater 4 in slot 1 of the bed (M140) or chamber (M141)</summary>
    private static string TwoSlotsConfig(string setCode) => $"{setCode} H3\n{setCode} P1 H4";

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
        await using Bench bench = await StartAsync(TwoSlotsConfig(SetCode(code)));

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
        await using Bench bench = await StartAsync(TwoSlotsConfig(setCode));

        Assert.That((await bench.Host.ExecuteCodeAsync(code)).Trim(), Is.Empty, "accepted");
        Assert.That(TemperatureMessages(bench), Is.Empty, "nothing is sent");
    }

    /// <summary>M190 and M191 R wait for the slot to cool to the target, and S does not</summary>
    /// <remarks>RRF truth: GCodes2.cpp case 190/191, Heat::HeaterAtSetTemperature</remarks>
    [TestCase("M190")]
    [TestCase("M191")]
    public async Task WaitsWhileCoolingOnlyForR(string code)
    {
        await using Bench bench = await StartAsync(TwoSlotsConfig(SetCode(code)));
        HeatManager heat = bench.Host.Services.GetRequiredService<HeatManager>();
        await bench.ReportHeaterAsync(3, HeaterMode.Stable, 80.0f);

        Assert.That((await bench.Host.ExecuteCodeAsync($"{code} S60")).Trim(), Is.Empty, "a heater above an S target is not waited for");

        Task<string> waiting = bench.Host.ExecuteCodeAsync($"{code} R60");
        await bench.CanMaster.WaitUntilAsync(() => heat.IsWaitingForTemperatures, what: $"{code} R waiting");
        Assert.That(waiting.IsCompleted, Is.False, "a heater above an R target is waited for");
        await bench.ReportHeaterAsync(3, HeaterMode.Stable, 61.0f);
        Assert.That((await waiting).Trim(), Is.Empty, "within 1.5C ends the wait");
    }

    /// <summary>M190 and M191 refuse a faulted heater, and from a channel that is not a file do not wait for it</summary>
    /// <remarks>RRF truth: Heater::SetActiveOrStandby, and HeaterAtSetTemperature waits on a fault only for a file channel</remarks>
    [TestCase("M190")]
    [TestCase("M191")]
    public async Task FaultedHeaterIsNotWaitedForOutsideAFile(string code)
    {
        await using Bench bench = await StartAsync(TwoSlotsConfig(SetCode(code)));
        await bench.ReportHeaterAsync(3, HeaterMode.Fault, 25.0f);

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
        await using Bench bench = await StartAsync($"{SetCode(code)} H3:4");
        HeatManager heat = bench.Host.Services.GetRequiredService<HeatManager>();
        await bench.ReportHeaterAsync(3, HeaterMode.Off, 25.0f);
        await bench.ReportHeaterAsync(4, HeaterMode.Off, 25.0f);

        Task<string> waiting = bench.Host.ExecuteCodeAsync($"{code} S50");
        await bench.CanMaster.WaitUntilAsync(() => heat.IsWaitingForTemperatures, what: $"{code} waiting");
        Assert.That(TemperatureMessages(bench).Select(m => (m.HeaterNumber, m.Command, m.Function, m.SetPoint)),
                    Is.EqualTo(new[] { ((byte)3, CanMessageSetHeaterTemperatureV1.CommandOn, (byte)function, 50.0f),
                                       ((byte)4, CanMessageSetHeaterTemperatureV1.CommandOn, (byte)function, 50.0f) }),
                    "both heaters are switched on before the wait");

        await bench.ReportHeaterAsync(3, HeaterMode.Stable, 50.0f);
        Assert.That(waiting.IsCompleted, Is.False, "heater 4 is still cold");
        await bench.ReportHeaterAsync(4, HeaterMode.Stable, 50.0f);
        Assert.That((await waiting).Trim(), Is.Empty, "both at the target ends the wait");
    }

    /// <summary>M190 and M191 refuse a temperature above the heater's limit, send nothing and wait for nothing</summary>
    /// <remarks>RRF truth: Heater::SetTemperature throws; the default limits are 125C for a bed and 100C for a chamber</remarks>
    [TestCase("M190")]
    [TestCase("M191")]
    public async Task WaitRefusesTemperatureAboveLimit(string code)
    {
        await using Bench bench = await StartAsync(TwoSlotsConfig(SetCode(code)));

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
        await using Bench bench = await StartAsync(TwoSlotsConfig(SetCode(code)));

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ExecuteCodeAsync($"{code} P{slot} S50"), Does.StartWith("Error"), "the slot is refused");
            Assert.That(TemperatureMessages(bench), Is.Empty, "nothing is sent");
        });
    }
}
