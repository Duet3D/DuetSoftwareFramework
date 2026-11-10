using System.IO;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using HeaterModel = DuetAPI.ObjectModel.HeaterModel;
using SystemTests.Host;

namespace SystemTests.Scenarios.Codes;

/// <summary>
/// M307, the heater process model, asserted against RepRapFirmware's Heater::SetOrReportModel,
/// Heater::SetModel and FopDt (FOPDT.cpp, CANlib HeaterModel.cpp), and against the regression suite's
/// m307-* references, which run on a heater that has a sensor and an output but no function
/// </summary>
[TestFixture]
public class HeaterModelCodeTests : BenchFixture
{
    /// <summary>Heater 2 on board 1 with no function, as the regression cases set it up</summary>
    private const string HeaterConfig = """
        M308 S2 P"1.temp0" Y"thermistor" T100000 B4138
        M950 H2 C"1.out0" T2
        """;

    private static Task<JobBench> StartAsync(System.Action<ScriptedCanMaster>? prepareController = null)
        => JobControlBench.StartAsync(configExtra: HeaterConfig, prepareController: prepareController);

    private static Task<HeaterModel> ReadHeaterModelAsync(JobBench bench)
        => bench.Host.ReadModelAsync(model => model.Heat.Heaters[2]!.Model);

    /// <summary>
    /// A new heater has DefaultToolHeaterModel with its PID constants at the typical temperature, disabled,
    /// and M307 with only H says so without sending anything
    /// </summary>
    /// <remarks>
    /// RRF truth: FopDt::Reset sets DefaultToolHeaterModel (CANlib HeaterModel.h) through SetDefaultModel,
    /// which calculates the PID constants at typicalTemperature 220, then clears enabled. SetOrReportModel
    /// reports a disabled model as such. Regression: m307-report-heater-model, and the "from" values of
    /// m307-set-heater-model
    /// </remarks>
    [Test]
    public async Task NewHeaterHasDisabledDefaultModel()
    {
        await using JobBench bench = await StartAsync();

        HeaterModel heaterModel = await ReadHeaterModelAsync(bench);
        Assert.Multiple(() =>
        {
            Assert.That(heaterModel.Enabled, Is.False, "a new heater's model is disabled (RRF FopDt::Reset)");
            Assert.That(heaterModel.HeatingRate, Is.EqualTo(2.43).Within(1e-4), "heatingRate is DefaultToolHeaterModel's");
            Assert.That(heaterModel.CoolingRate, Is.EqualTo(0.56).Within(1e-4), "coolingRate is DefaultToolHeaterModel's");
            Assert.That(heaterModel.FanCoolingRate, Is.EqualTo(0.0).Within(1e-4), "fanCoolingRate is DefaultToolHeaterModel's");
            Assert.That(heaterModel.CoolingExp, Is.EqualTo(1.35).Within(1e-4), "coolingExp is DefaultToolHeaterModel's");
            Assert.That(heaterModel.DeadTime, Is.EqualTo(5.5).Within(1e-4), "deadTime is DefaultToolHeaterModel's");
            Assert.That(heaterModel.MaxPwm, Is.EqualTo(1.0).Within(1e-4), "maxPwm starts at 1 (RRF FopDt::SetDefaultModel)");
            Assert.That(heaterModel.StandardVoltage, Is.EqualTo(0.0).Within(1e-4), "standardVoltage is DefaultToolHeaterModel's");
            Assert.That(heaterModel.Inverted, Is.False, "a new model is not inverted (RRF FopDt::SetDefaultModel)");
            Assert.That(heaterModel.PID.Used, Is.True, "DefaultToolHeaterModel uses PID");
            Assert.That(heaterModel.PID.P, Is.EqualTo(0.052376).Within(1e-6), "pid.p at 220C (regression m307-set-heater-model)");
            Assert.That(heaterModel.PID.I, Is.EqualTo(0.00371).Within(1e-5), "pid.i at 220C (regression m307-set-heater-model)");
            Assert.That(heaterModel.PID.D, Is.EqualTo(0.202).Within(1e-3), "pid.d at 220C (regression m307-set-heater-model)");
        });

        string reply = (await bench.Host.ExecuteCodeAsync("M307 H2")).TrimEnd();
        Assert.Multiple(() =>
        {
            Assert.That(reply, Is.EqualTo("Heater 2 is disabled because its model is undefined"),
                        "M307 H reports a disabled model as such (RRF Heater::SetOrReportModel)");
            Assert.That(bench.CanMaster.CanMessages<CanMessageHeaterModelV3>(), Is.Empty, "a report sends nothing to the board");
        });
    }

    /// <summary>
    /// M307 with R, K, E, D, S and B sets the whole model, sends the whole model to the board, enables it,
    /// derives the PID constants at 100C, and reports it back
    /// </summary>
    /// <remarks>
    /// RRF truth: Heater::SetOrReportModel, FopDt::SetParameters (CalcPidConstants(100)), FopDt::SetupCanMessage
    /// and FopDt::AppendModelParameters. Regression: m307-set-heater-model
    /// </remarks>
    [Test]
    public async Task SetsModelAndSendsItWhole()
    {
        await using JobBench bench = await StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M307 H2 R2.5 K0.35:0.15 D5.5 E1.35 S1.0 B0");
        Assert.That(reply.Trim(), Is.Empty, "setting a valid model replies nothing (regression m307-set-heater-model)");

        (byte board, CanMessageHeaterModelV3 message) = bench.CanMaster.LastCanMessage<CanMessageHeaterModelV3>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(1), "the model goes to the board carrying the heater (RRF RemoteHeater::UpdateRemoteModel)");
            Assert.That(message.Heater, Is.EqualTo(2), "heater");
            Assert.That(message.Enabled, Is.True, "enabled");
            Assert.That(message.Inverted, Is.False, "inverted");
            Assert.That(message.MaxPwm, Is.EqualTo(1.0).Within(1e-6), "maxPwm");
            Assert.That(message.BasicModel.HeatingRate, Is.EqualTo(2.5).Within(1e-6), "basicModel.heatingRate");
            Assert.That(message.BasicModel.BasicCoolingRate, Is.EqualTo(0.35).Within(1e-6), "basicModel.basicCoolingRate");
            Assert.That(message.BasicModel.FanCoolingRate, Is.EqualTo(0.15).Within(1e-6), "basicModel.fanCoolingRate");
            Assert.That(message.BasicModel.CoolingRateExponent, Is.EqualTo(1.35).Within(1e-6), "basicModel.coolingRateExponent");
            Assert.That(message.BasicModel.DeadTime, Is.EqualTo(5.5).Within(1e-6), "basicModel.deadTime");
            Assert.That(message.BasicModel.TemperatureCoefficient, Is.EqualTo(0.0).Within(1e-6), "basicModel.temperatureCoefficient");
            Assert.That(message.BasicModel.TypicalTemperature, Is.EqualTo(220.0).Within(1e-6), "basicModel.typicalTemperature");
            Assert.That(message.BasicModel.StandardVoltage, Is.EqualTo(0.0).Within(1e-6), "basicModel.standardVoltage");
            Assert.That(message.BasicModel.Fzero, Is.EqualTo(0.0).Within(1e-6), "basicModel.fzero");
            Assert.That(message.BasicModel.UsePid, Is.True, "basicModel.usePid");
        });

        HeaterModel heaterModel = await ReadHeaterModelAsync(bench);
        Assert.Multiple(() =>
        {
            Assert.That(heaterModel.Enabled, Is.True, "M307 enables the model (RRF FopDt::SetParameters)");
            Assert.That(heaterModel.HeatingRate, Is.EqualTo(2.5).Within(1e-4), "R sets heatingRate");
            Assert.That(heaterModel.CoolingRate, Is.EqualTo(0.35).Within(1e-4), "K sets coolingRate");
            Assert.That(heaterModel.FanCoolingRate, Is.EqualTo(0.15).Within(1e-4), "K's second value sets fanCoolingRate");
            Assert.That(heaterModel.CoolingExp, Is.EqualTo(1.35).Within(1e-4), "E sets coolingExp");
            Assert.That(heaterModel.DeadTime, Is.EqualTo(5.5).Within(1e-4), "D sets deadTime");
            Assert.That(heaterModel.MaxPwm, Is.EqualTo(1.0).Within(1e-4), "S sets maxPwm");
            Assert.That(heaterModel.PID.Used, Is.True, "B0 uses PID");
            Assert.That(heaterModel.PID.P, Is.EqualTo(0.050909).Within(1e-6), "pid.p at 100C (regression m307-set-heater-model)");
            Assert.That(heaterModel.PID.I, Is.EqualTo(0.003017).Within(1e-6), "pid.i at 100C (regression m307-set-heater-model)");
            Assert.That(heaterModel.PID.D, Is.EqualTo(0.196).Within(1e-3), "pid.d at 100C (regression m307-set-heater-model)");
        });

        Assert.That((await bench.Host.ExecuteCodeAsync("M307 H2")).TrimEnd(), Is.EqualTo(
            "Heater 2: heating rate 2.500, cooling rate 0.350/0.150, dead time 5.50, max PWM 1.00, mode PID, calibrated at 0.0V\n"
            + "Predicted max temperature rise 429°C\n"
            + "PID parameters: heating P13.0 I0.326 D50.0, steady P13.0 I0.769 D50.0"),
            "M307 H reports the model (regression m307-set-heater-model)");
    }

    /// <summary>
    /// The legacy A (gain) and C (time constant) form: C sets the cooling rate to 100/C and the exponent to 1,
    /// and A without R sets the heating rate to A times that cooling rate over 100
    /// </summary>
    /// <remarks>RRF truth: Heater::SetOrReportModel. Regression: m307-legacy-gain-and-time-constant</remarks>
    [Test]
    public async Task LegacyGainAndTimeConstant()
    {
        await using JobBench bench = await StartAsync();

        await bench.Host.ExecuteCodeAsync("M307 H2 A340 C140 D5.5 S1.0 B0");
        CanMessageHeaterModelV3 message = bench.CanMaster.LastCanMessage<CanMessageHeaterModelV3>().Message;
        HeaterModel heaterModel = await ReadHeaterModelAsync(bench);
        Assert.Multiple(() =>
        {
            Assert.That(message.BasicModel.BasicCoolingRate, Is.EqualTo(100.0 / 140.0).Within(1e-5), "C is 100/C");
            Assert.That(message.BasicModel.FanCoolingRate, Is.EqualTo(0.0).Within(1e-6), "one C value means no fan cooling");
            Assert.That(message.BasicModel.CoolingRateExponent, Is.EqualTo(1.0).Within(1e-6), "C forces the exponent to 1");
            Assert.That(message.BasicModel.HeatingRate, Is.EqualTo(340.0 / 140.0).Within(1e-5), "A is the gain times the cooling rate");
            Assert.That(heaterModel.CoolingExp, Is.EqualTo(1.0).Within(1e-6), "coolingExp follows C");
            Assert.That(heaterModel.CoolingRate, Is.EqualTo(100.0 / 140.0).Within(1e-5), "coolingRate follows C");
            Assert.That(heaterModel.HeatingRate, Is.EqualTo(340.0 / 140.0).Within(1e-5), "heatingRate follows A");
            Assert.That(heaterModel.PID.I, Is.EqualTo(0.003721).Within(1e-6), "pid.i (regression m307-legacy-gain-and-time-constant)");
            Assert.That(heaterModel.PID.P, Is.EqualTo(0.052406).Within(1e-6), "pid.p (regression m307-legacy-gain-and-time-constant)");
        });

        Assert.That((await bench.Host.ExecuteCodeAsync("M307 H2")).TrimEnd(), Is.EqualTo(
            "Heater 2: heating rate 2.429, cooling rate 0.714, dead time 5.50, max PWM 1.00, mode PID, calibrated at 0.0V\n"
            + "Predicted max temperature rise 340°C\n"
            + "PID parameters: heating P13.4 I0.482 D51.4, steady P13.4 I0.949 D51.4"),
            "M307 H reports the legacy model (regression m307-legacy-gain-and-time-constant)");
    }

    /// <summary>A second C value is the time constant with the fan on, giving the fan cooling rate</summary>
    /// <remarks>RRF truth: Heater::SetOrReportModel, fanCoolingRate = 100/C[1] - basicCoolingRate</remarks>
    [Test]
    public async Task SecondTimeConstantGivesFanCoolingRate()
    {
        await using JobBench bench = await StartAsync();

        await bench.Host.ExecuteCodeAsync("M307 H2 R2.5 C140:100");
        HeaterModel heaterModel = await ReadHeaterModelAsync(bench);
        Assert.Multiple(() =>
        {
            Assert.That(heaterModel.CoolingRate, Is.EqualTo(100.0 / 140.0).Within(1e-5), "coolingRate is 100/C[0]");
            Assert.That(heaterModel.FanCoolingRate, Is.EqualTo(1.0 - 100.0 / 140.0).Within(1e-5), "fanCoolingRate is 100/C[1] less that");
        });
    }

    /// <summary>
    /// K with one value clears the fan cooling rate, E is read only alongside K, and K takes precedence over C
    /// </summary>
    /// <remarks>RRF truth: Heater::SetOrReportModel reads E inside the K branch and C only without K</remarks>
    [Test]
    public async Task CoolingRateParameters()
    {
        await using JobBench bench = await StartAsync();

        await bench.Host.ExecuteCodeAsync("M307 H2 K0.35:0.15");
        await bench.Host.ExecuteCodeAsync("M307 H2 K0.4 C200");
        HeaterModel heaterModel = await ReadHeaterModelAsync(bench);
        Assert.Multiple(() =>
        {
            Assert.That(heaterModel.CoolingRate, Is.EqualTo(0.4).Within(1e-5), "K wins over C");
            Assert.That(heaterModel.FanCoolingRate, Is.EqualTo(0.0).Within(1e-5), "a single K value clears fanCoolingRate");
            Assert.That(heaterModel.CoolingExp, Is.EqualTo(1.35).Within(1e-5), "C did not force the exponent");
        });

        await bench.Host.ExecuteCodeAsync("M307 H2 E1.5 D6");
        Assert.Multiple(async () =>
        {
            Assert.That((await ReadHeaterModelAsync(bench)).CoolingExp, Is.EqualTo(1.35).Within(1e-5), "E without K is ignored");
            Assert.That((await ReadHeaterModelAsync(bench)).DeadTime, Is.EqualTo(6.0).Within(1e-5), "the rest of the code still applies");
        });
    }

    /// <summary>
    /// E alone is not a parameter M307 acts on, so M307 H E only reports, and so does an unknown letter
    /// </summary>
    /// <remarks>RRF truth: Heater::SetOrReportModel. Regression: m307-inverse-temperature-control (M307 H2 F10)</remarks>
    [Test]
    public async Task ParametersItDoesNotReadOnlyReport()
    {
        await using JobBench bench = await StartAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync("M307 H2 E1.5")).TrimEnd(),
                    Is.EqualTo("Heater 2 is disabled because its model is undefined"), "E without K only reports");
        await bench.Host.ExecuteCodeAsync("M307 H2 B0");
        Assert.That((await bench.Host.ExecuteCodeAsync("M307 H2 F10")).TrimEnd(), Does.StartWith("Heater 2: heating rate 2.430"),
                    "an unknown parameter only reports (regression m307-inverse-temperature-control)");
        Assert.That(bench.CanMaster.CanMessages<CanMessageHeaterModelV3>(), Has.Count.EqualTo(1), "only B0 was sent");
    }

    /// <summary>
    /// B1 selects bang-bang on the current model and enables it, and the report then has no PID line; B0
    /// goes back to PID
    /// </summary>
    /// <remarks>RRF truth: Heater::SetOrReportModel, FopDt::AppendModelParameters. Regression: m307-disable-heater-model</remarks>
    [Test]
    public async Task BangBangAndPid()
    {
        await using JobBench bench = await StartAsync();

        await bench.Host.ExecuteCodeAsync("M307 H2 B1");
        CanMessageHeaterModelV3 message = bench.CanMaster.LastCanMessage<CanMessageHeaterModelV3>().Message;
        Assert.Multiple(async () =>
        {
            Assert.That(message.BasicModel.UsePid, Is.False, "B1 sends usePid off");
            Assert.That(message.Enabled, Is.True, "B1 still enables the model");
            Assert.That(message.BasicModel.HeatingRate, Is.EqualTo(2.43).Within(1e-6), "the rest is the current model");
            Assert.That((await ReadHeaterModelAsync(bench)).PID.Used, Is.False, "pid.used follows B1");
            Assert.That((await ReadHeaterModelAsync(bench)).PID.I, Is.EqualTo(0.003412).Within(1e-6),
                        "pid.i is recalculated at 100C (regression m307-disable-heater-model)");
        });
        Assert.That((await bench.Host.ExecuteCodeAsync("M307 H2")).TrimEnd(), Is.EqualTo(
            "Heater 2: heating rate 2.430, cooling rate 0.560, dead time 5.50, max PWM 1.00, mode bang-bang, calibrated at 0.0V\n"
            + "Predicted max temperature rise 296°C"),
            "a bang-bang model reports no PID parameters (regression m307-disable-heater-model)");

        await bench.Host.ExecuteCodeAsync("M307 H2 B0");
        Assert.That(bench.CanMaster.LastCanMessage<CanMessageHeaterModelV3>().Message.BasicModel.UsePid, Is.True, "B0 sends usePid on");
        Assert.That((await bench.Host.ExecuteCodeAsync("M307 H2")).TrimEnd(), Is.EqualTo(
            "Heater 2: heating rate 2.430, cooling rate 0.560, dead time 5.50, max PWM 1.00, mode PID, calibrated at 0.0V\n"
            + "Predicted max temperature rise 296°C\n"
            + "PID parameters: heating P13.4 I0.405 D51.4, steady P13.4 I0.870 D51.4"),
            "a PID model reports its PID parameters (regression m307-disable-heater-model)");
    }

    /// <summary>
    /// I1 and I3 invert the output, I0 and I2 do not, and a later M307 without I clears the inversion
    /// </summary>
    /// <remarks>RRF truth: Heater::SetOrReportModel's inversionParameter starts at 0. Regression: m307-inverse-temperature-control</remarks>
    [TestCase("I1", true)]
    [TestCase("I3", true)]
    [TestCase("I2", false)]
    [TestCase("I0", false)]
    public async Task InversionParameter(string parameter, bool inverted)
    {
        await using JobBench bench = await StartAsync();

        await bench.Host.ExecuteCodeAsync($"M307 H2 {parameter}");
        Assert.Multiple(async () =>
        {
            Assert.That(bench.CanMaster.LastCanMessage<CanMessageHeaterModelV3>().Message.Inverted, Is.EqualTo(inverted), "message inverted");
            Assert.That((await ReadHeaterModelAsync(bench)).Inverted, Is.EqualTo(inverted), "model.inverted");
            Assert.That(await bench.Host.ExecuteCodeAsync("M307 H2"),
                        inverted ? Does.Contain("mode PID, reverse control, calibrated") : Does.Not.Contain("reverse control"),
                        "the report says reverse control when inverted (regression m307-inverse-temperature-control)");
        });

        await bench.Host.ExecuteCodeAsync("M307 H2 D5.5");
        Assert.That((await ReadHeaterModelAsync(bench)).Inverted, Is.False, "M307 without I is not inverted");
    }

    /// <summary>V sets the calibration voltage the board compensates the supply for, and V0 clears it</summary>
    /// <remarks>RRF truth: Heater::SetOrReportModel. Regression: m307-supply-voltage-compensation</remarks>
    [Test]
    public async Task SupplyVoltageCompensation()
    {
        await using JobBench bench = await StartAsync();

        await bench.Host.ExecuteCodeAsync("M307 H2 R2.5 K0.35 D5.5 V24.0");
        Assert.Multiple(async () =>
        {
            Assert.That(bench.CanMaster.LastCanMessage<CanMessageHeaterModelV3>().Message.BasicModel.StandardVoltage,
                        Is.EqualTo(24.0).Within(1e-6), "V is sent as standardVoltage");
            Assert.That((await ReadHeaterModelAsync(bench)).StandardVoltage, Is.EqualTo(24.0).Within(1e-6), "V sets model.standardVoltage");
            Assert.That((await bench.Host.ExecuteCodeAsync("M307 H2")).TrimEnd(), Is.EqualTo(
                "Heater 2: heating rate 2.500, cooling rate 0.350, dead time 5.50, max PWM 1.00, mode PID, calibrated at 24.0V\n"
                + "Predicted max temperature rise 429°C\n"
                + "PID parameters: heating P13.0 I0.311 D50.0, steady P13.0 I0.752 D50.0"),
                "the report gives the calibration voltage (regression m307-supply-voltage-compensation)");
        });

        await bench.Host.ExecuteCodeAsync("M307 H2 V0");
        Assert.Multiple(async () =>
        {
            Assert.That(bench.CanMaster.LastCanMessage<CanMessageHeaterModelV3>().Message.BasicModel.StandardVoltage,
                        Is.EqualTo(0.0).Within(1e-6), "V0 is sent");
            Assert.That(await bench.Host.ExecuteCodeAsync("M307 H2"), Does.Contain("calibrated at 0.0V"), "V0 clears the voltage");
        });
    }

    /// <summary>
    /// A bed heater's report leaves out the calibration voltage, which only tool heaters give, and its model carries the
    /// bed default's typical temperature
    /// </summary>
    /// <remarks>RRF truth: Heater::SetOrReportModel passes GetHeaterFunction(heater) == HeaterFunction::tool</remarks>
    [Test]
    public async Task BedHeaterReportOmitsVoltage()
    {
        await using JobBench bench = await StartAsync();

        await bench.Host.ExecuteCodeAsync("M140 H2");
        await bench.Host.ExecuteCodeAsync("M307 H2 R0.5 K0.15 D10 V24");
        Assert.Multiple(async () =>
        {
            Assert.That((await bench.Host.ExecuteCodeAsync("M307 H2")).TrimEnd(), Does.StartWith(
                "Heater 2: heating rate 0.500, cooling rate 0.150, dead time 10.00, max PWM 1.00, mode PID\n"),
                "a bed heater's report has no calibration voltage");
            Assert.That(bench.CanMaster.LastCanMessage<CanMessageHeaterModelV3>().Message.BasicModel.TypicalTemperature,
                        Is.EqualTo(60.0).Within(1e-6), "a bed heater's model carries DefaultBedHeaterModel's typical temperature");
        });
    }

    /// <summary>
    /// A model that fails FopDt's checks is refused with the reason, and neither the board nor the object
    /// model hears of it
    /// </summary>
    /// <remarks>RRF truth: FopDt::SetParameters, "bad model parameters: %s"</remarks>
    [TestCase("M307 H2 R0.05", "estimated temperature rise too small")]
    [TestCase("M307 H2 C0", "estimated temperature rise too small")]
    [TestCase("M307 H2 K0.56:-0.1", "fan reduces cooling rate")]
    [TestCase("M307 H2 K0.56 E0.9", "cooling rate exponent out of range")]
    [TestCase("M307 H2 K0.56 E1.7", "cooling rate exponent out of range")]
    [TestCase("M307 H2 D0.005", "dead time too small")]
    [TestCase("M307 H2 D90", "dead time too close to cooling time constant")]
    [TestCase("M307 H2 S0", "PWM out of range")]
    [TestCase("M307 H2 S1.1", "PWM out of range")]
    public async Task RefusesBadModel(string code, string reason)
    {
        await using JobBench bench = await StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync(code);
        HeaterModel heaterModel = await ReadHeaterModelAsync(bench);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain($"bad model parameters: {reason}"), "the reason is given");
            Assert.That(bench.CanMaster.CanMessages<CanMessageHeaterModelV3>(), Is.Empty, "nothing is sent");
            Assert.That(heaterModel.Enabled, Is.False, "the model stays disabled");
            Assert.That(heaterModel.HeatingRate, Is.EqualTo(2.43).Within(1e-4), "heatingRate is unchanged");
            Assert.That(heaterModel.CoolingRate, Is.EqualTo(0.56).Within(1e-4), "coolingRate is unchanged");
            Assert.That(heaterModel.DeadTime, Is.EqualTo(5.5).Within(1e-4), "deadTime is unchanged");
            Assert.That(heaterModel.MaxPwm, Is.EqualTo(1.0).Within(1e-4), "maxPwm is unchanged");
        });
    }

    /// <summary>R must be greater than zero</summary>
    /// <remarks>RRF truth: Heater::SetOrReportModel reads R with GetPositiveFValue</remarks>
    [Test]
    public async Task RefusesNonPositiveHeatingRate()
    {
        await using JobBench bench = await StartAsync();

        Assert.That(await bench.Host.ExecuteCodeAsync("M307 H2 R0"), Does.StartWith("Error"), "R0 is refused");
        Assert.That(bench.CanMaster.CanMessages<CanMessageHeaterModelV3>(), Is.Empty, "nothing is sent");
    }

    /// <summary>A heater that does not exist is refused</summary>
    [Test]
    public async Task RefusesMissingHeater()
    {
        await using JobBench bench = await StartAsync();

        Assert.That(await bench.Host.ExecuteCodeAsync("M307 H5 B0"), Does.StartWith("Error").And.Contain("Heater 5 not found"),
                    "an undefined heater is refused");
    }

    /// <summary>
    /// A board that refuses the model has its refusal reported, and the model stays as it was set, as RRF keeps it
    /// on the main board
    /// </summary>
    /// <remarks>RRF truth: Heater::SetModel sets the local model before RemoteHeater::UpdateRemoteModel</remarks>
    [Test]
    public async Task ReportsBoardRefusal()
    {
        await using JobBench bench = await StartAsync(canMaster =>
            canMaster.ReportWith<CanMessageHeaterModelV3>("bad model parameters: PWM out of range", CodeResult.Error));

        string reply = await bench.Host.ExecuteCodeAsync("M307 H2 R2.5 K0.35 D5.5");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error").And.Contain("bad model parameters: PWM out of range"), "the board's refusal is the reply");
            Assert.That((await ReadHeaterModelAsync(bench)).HeatingRate, Is.EqualTo(2.5).Within(1e-4), "the model was still set");
            Assert.That((await ReadHeaterModelAsync(bench)).Enabled, Is.True, "the model was still enabled");
        });
    }

    /// <summary>
    /// A model that would take the heater far past its over-temperature limit is set, with a warning
    /// </summary>
    /// <remarks>
    /// RRF truth: Heater::SetModel warns when the predicted maximum (rise + 25C) exceeds
    /// (GetHighestTemperatureLimit() - 25) * 1.5 + 50. The default model predicts 296C rise, so 321C; a 100C
    /// limit allows 162.5C
    /// </remarks>
    [Test]
    public async Task WarnsAboutPredictedTemperatureAboveLimit()
    {
        await using JobBench bench = await StartAsync();

        await bench.Host.ExecuteCodeAsync("M143 H2 S100");
        string reply = (await bench.Host.ExecuteCodeAsync("M307 H2 B0")).TrimEnd();
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Warning").And.EndWith("Heater 2 predicted maximum temperature at full power is 321°C"),
                        "the predicted maximum is a warning");
            Assert.That((await ReadHeaterModelAsync(bench)).Enabled, Is.True, "the model was set");
        });
    }

    /// <summary>
    /// M500 writes every enabled model as the M307 command that sets it, and no disabled one
    /// </summary>
    /// <remarks>RRF truth: Heat::WriteModelParameters and FopDt::AppendM307Command</remarks>
    [Test]
    public async Task M500WritesEnabledModels()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: HeaterConfig + """

            M308 S3 P"1.temp1" Y"thermistor"
            M950 H3 C"1.out1" T3
            """);

        await bench.Host.ExecuteCodeAsync("M307 H2 R2.5 K0.35:0.15 D5.5 E1.35 S0.9 B0 I1 V24");
        await bench.Host.ExecuteCodeAsync("M500");
        string written = await File.ReadAllTextAsync(Path.Combine(bench.Host.Sd.Root, "sys", "config-override.g"));
        Assert.Multiple(() =>
        {
            Assert.That(written, Does.Contain("; Heater model parameters\nM307 H2 R2.500 K0.350:0.150 D5.50 E1.35 S0.90 B0 I1 V24.0\n"),
                        "an enabled model is written as its M307 command");
            Assert.That(written, Does.Not.Contain("M307 H3"), "a disabled model is not written");
        });
    }
}
