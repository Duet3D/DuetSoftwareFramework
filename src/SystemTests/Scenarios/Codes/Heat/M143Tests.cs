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

/// <summary>M143, the heater monitors</summary>
[TestFixture]
[GCode("M143")]
public class M143Tests : BenchFixture
{
    /// <summary>
    /// M143 configures heater monitors: a bare S sets monitor 0 as a tooHigh fault limit that
    /// heat.heaters[].max reports, and P/T/A/C/S fill another monitor whose tooLow limit
    /// heat.heaters[].min reports
    /// </summary>
    /// <remarks>
    /// RRF truth: Heat::HandleM143 and Heater::ConfigureMonitor (Heater.cpp): defaults are
    /// monitor 0, the heater's own sensor, action GenerateFault (0) and trigger
    /// TemperatureExceeded. The monitor fields are Heater::objectModelTable's monitors[] entries
    /// (action as integer, condition via HeaterMonitor::GetTriggerName), and max/min are
    /// GetHighestTemperatureLimit and GetLowestTemperatureLimit over the monitors
    /// </remarks>
    [Test]
    public async Task M143ConfiguresHeaterMonitors()
    {
        await using Bench bench = await Bench.StartAsync(HeatConfig);

        await bench.Host.ExecuteCodeAsync("M143 H0 S110");
        await bench.Host.ExecuteCodeAsync("M143 H0 P1 T0 A2 C1 S5");

        Heater bed = ReadHeater(bench.Host, 0)!;
        HeaterMonitor? monitor0 = bed.Monitors.Count > 0 ? bed.Monitors[0] : null;
        HeaterMonitor? monitor1 = bed.Monitors.Count > 1 ? bed.Monitors[1] : null;
        Assert.Multiple(async () =>
        {
            Assert.That(monitor0?.Limit, Is.EqualTo(110.0f).Within(0.01),
                        "M143 S sets heat.heaters[0].monitors[0].limit (RRF Heater.cpp ConfigureMonitor)");
            Assert.That(monitor0?.Condition, Is.EqualTo(HeaterMonitorCondition.TooHigh),
                        "M143 defaults to the TemperatureExceeded trigger (RRF HeaterMonitor.cpp GetTriggerName)");
            Assert.That(monitor0?.Action, Is.EqualTo(HeaterMonitorAction.GenerateFault),
                        "M143 defaults to the GenerateFault action (RRF Heater.cpp ConfigureMonitor)");
            Assert.That(monitor0?.Sensor, Is.EqualTo(0),
                        "M143 defaults to the heater's own sensor (RRF Heater.cpp ConfigureMonitor)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Heat.Heaters[0]!.Max), Is.EqualTo(110).Within(0.01),
                        "heat.heaters[0].max reports the tooHigh monitor limit (RRF Heater.cpp GetHighestTemperatureLimit)");
            Assert.That(monitor1?.Limit, Is.EqualTo(5.0f).Within(0.01),
                        "M143 P1 S sets heat.heaters[0].monitors[1].limit (RRF Heater.cpp ConfigureMonitor)");
            Assert.That(monitor1?.Condition, Is.EqualTo(HeaterMonitorCondition.TooLow),
                        "M143 C1 selects the TemperatureTooLow trigger (RRF HeaterMonitor.cpp GetTriggerName)");
            Assert.That(monitor1?.Action, Is.EqualTo(HeaterMonitorAction.TemporarySwitchOff),
                        "M143 A2 selects the TemporarySwitchOff action (RRF Heater.cpp ConfigureMonitor)");
            Assert.That(monitor1?.Sensor, Is.EqualTo(0),
                        "M143 T names the sensor the monitor watches (RRF Heater.cpp ConfigureMonitor)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Heat.Heaters[0]!.Min), Is.EqualTo(5).Within(0.01),
                        "heat.heaters[0].min reports the tooLow monitor limit (RRF Heater.cpp GetLowestTemperatureLimit)");
        });
    }
}
