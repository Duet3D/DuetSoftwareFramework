using System.Globalization;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Heat;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Heat.HeatSetup;

namespace SystemTests.Scenarios.Codes.Heat;

/// <summary>M950 H, creating a heater</summary>
[TestFixture]
[GCode("M950")]
public class M950HeaterTests : BenchFixture
{
    /// <summary>
    /// M950 H creates a heater bound to its sensor: heat.heaters[] gains an entry that is off,
    /// whose model is disabled until the heater is given a function, and whose temperature limits
    /// are wide open because no monitor is set yet
    /// </summary>
    /// <remarks>
    /// RRF truth: Heat::ConfigureHeater (Heat.cpp) creates the heater from H/C/T; the reported
    /// fields are Heater::objectModelTable (Heater.cpp). The model starts disabled (FopDt.cpp,
    /// "disabled until the user declares the heater to be a bed, chamber or tool heater") and
    /// monitor defaults only arrive with Heater::SetFunction. With every monitor disabled,
    /// GetHighestTemperatureLimit returns BadErrorTemperature (2000) and GetLowestTemperatureLimit
    /// returns ABS_ZERO (-273.15). heat.heaters[].port and .frequency are a documented DSF
    /// addition (rrf-differences.md section 3, the object model must recreate the machine)
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M950CreatesHeaterBoundToSensor()
    {
        await using Bench bench = await Bench.StartAsync(HeatConfig);
        await bench.Host.ExecuteCodeAsync($"M308 S3 P\"{Expansion.Temp(Machines.DriverBoard, 3)}\" Y\"thermistor\"");
        await bench.Host.ExecuteCodeAsync($"M950 H3 C\"{Expansion.Out(Machines.DriverBoard, 3)}\" T3");

        Heater? heater = await bench.Host.ReadModelAsync(model => model.Heat.Heaters[3]);
        Assert.Multiple(() =>
        {
            Assert.NotNull(heater);
            Assert.That(heater!.Sensor, Is.EqualTo(3),
                        "M950 T binds heat.heaters[3].sensor (RRF Heat.cpp ConfigureHeater)");
            Assert.That(heater!.State, Is.EqualTo(HeaterState.Off),
                        "a new heater starts off (RRF Heater.cpp GetStatus)");
            Assert.That(heater!.Model.Enabled, Is.False,
                        "M950 H alone leaves heat.heaters[3].model.enabled false until a function is assigned (RRF FOPDT.cpp)");
            Assert.That(heater!.Monitors, Has.Count.EqualTo(3),
                        "a heater carries MaxMonitorsPerHeater monitor slots (RRF Heater.cpp objectModelTable monitors[])");
            Assert.That(heater!.Monitors.Count > 0
                            ? heater!.Monitors[0]?.Condition
                            : null,
                        Is.EqualTo(HeaterMonitorCondition.Disabled),
                        "a new heater's monitors are all disabled (RRF HeaterMonitor.cpp GetTriggerName)");
            Assert.That(heater!.Max, Is.EqualTo(2000).Within(0.01),
                        "with no monitor heat.heaters[3].max is BadErrorTemperature (RRF Heater.cpp GetHighestTemperatureLimit)");
            Assert.That(heater!.Min, Is.EqualTo(-273.15).Within(0.01),
                        "with no monitor heat.heaters[3].min is ABS_ZERO (RRF Heater.cpp GetLowestTemperatureLimit)");
            Assert.That(heater!.Port, Is.EqualTo($"{Expansion.Out(Machines.DriverBoard, 3)}"),
                        "M950 C is kept in heat.heaters[3].port (DSF addition, rrf-differences.md section 3)");
            Assert.That(heater!.Frequency, Is.EqualTo(250),
                        "heat.heaters[3].frequency defaults to DefaultHeaterPwmFreq (DSF addition, rrf-differences.md section 3)");
        });
    }
}
