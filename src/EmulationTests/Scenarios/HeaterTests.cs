using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetEmulation;
using DuetEmulation.Boards;
using DuetEmulation.Traces;
using NUnit.Framework;

namespace EmulationTests.Scenarios;

/// <summary>
/// A heater on an expansion board controlling a thermal model: the board's control loop drives the
/// heater output, the model heats, and the thermistor input reads the result
/// </summary>
[TestFixture(Stack.Rrf, Category = nameof(Stack.Rrf))]
[TestFixture(Stack.Dsf, Category = nameof(Stack.Dsf))]
[Category("Emulation")]
public class HeaterTests(Stack stack)
{
    private const int Address = 1;
    private const int Heater = 1;
    private const int Sensor = 1;
    private const double Target = 80;

    private Bench _bench = null!;

    [OneTimeSetUp]
    public async Task StartAsync() =>
        _bench = await Bench.StartAsync(
            stack,
            new Dictionary<int, BoardType> { [Address] = BoardTypes.Exp3hc },
            $"M308 S{Sensor} P\"{Address}.temp0\" Y\"thermistor\" T100000 B4725\n" +
            $"M950 H{Heater} C\"{Address}.out0\" T{Sensor}\n" +
            ThermalModel.Fast.M307(Heater) + "\n" +
            $"M140 P0 H{Heater}\n",
            Runs.Directory(this));

    [OneTimeTearDown]
    public async Task StopAsync() => await _bench.DisposeAsync();

    [Test]
    public async Task TheBedHeaterReachesAndHoldsItsTarget()
    {
        await _bench.Io.AttachThermalModelAsync(Address, "out0", "temp0", ThermalModel.Fast);
        _bench.System.Io.SetMark();

        CodeReply reply = await _bench.SendCodeAsync($"M140 S{Target}");
        Assert.That(reply.Severity, Is.EqualTo(MessageType.Success), reply.ToString());

        // Rising 55C at up to 20C/s takes a few seconds; then the loop has to settle
        await _bench.WaitUntilAsync(async () => await _bench.Io.ThermalModelTemperatureAsync(Address, "out0") >= Target - 1,
                                    TimeSpan.FromSeconds(30), $"the heater to reach {Target - 1}C");
        await _bench.WaitAsync(TimeSpan.FromSeconds(10));

        double temperature = await _bench.Io.ThermalModelTemperatureAsync(Address, "out0");
        Assert.That(temperature, Is.EqualTo(Target).Within(2), "the model holds the target");
        Assert.That((await _bench.GetObjectModelAsync($"heat.heaters[{Heater}].current"))!.GetValue<double>(), Is.EqualTo(temperature).Within(1),
                    "the firmware reads what the model is at");
        Assert.That((await _bench.GetObjectModelAsync($"heat.heaters[{Heater}].state"))!.GetValue<string>(), Is.EqualTo("active"));

        // The loop drove the heater with PWM, holding it below full power once it had settled
        PinOutput output = await _bench.Io.OutputAsync(Address, "out0");
        Assert.That(output.Mode, Is.EqualTo(PinMode.Pwm), output.ToString());
        Assert.That(output.Duty, Is.InRange(0.01, 0.5));
        Assert.That(_bench.System.Io.Find(Address, "out0").Count(e => e.Output.Mode == PinMode.Pwm), Is.GreaterThan(10));

        await _bench.SendCodeAsync("M140 S-273.1");
    }
}
