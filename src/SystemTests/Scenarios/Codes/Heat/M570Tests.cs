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

/// <summary>M570, heater fault detection</summary>
[TestFixture]
[GCode("M570")]
public class M570Tests : BenchFixture
{
    /// <summary>M570 sets the fault detection parameters the heater reports</summary>
    /// <remarks>
    /// RRF truth: Heater::ConfigureFaultDetectionParameters (Heater.cpp): P is
    /// maxHeatingFaultTime, T is maxTempExcursion, R is maxBadReadings; all three are in
    /// Heater::objectModelTable. The parameterless report quotes the same three values
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M570ConfiguresFaultDetection()
    {
        await using Bench bench = await Bench.StartAsync(HeatConfig);
        await bench.Host.ExecuteCodeAsync("M570 H0 P120 T15 R5");

        Heater? heater = await bench.Host.ReadModelAsync(model => model.Heat.Heaters[0]);
        Assert.Multiple(async () =>
        {
            Assert.That(heater!.MaxHeatingFaultTime, Is.EqualTo(120).Within(0.01),
                        "M570 P sets heat.heaters[0].maxHeatingFaultTime (RRF Heater.cpp ConfigureFaultDetectionParameters)");
            Assert.That(heater!.MaxTempExcursion, Is.EqualTo(15).Within(0.01),
                        "M570 T sets heat.heaters[0].maxTempExcursion (RRF Heater.cpp ConfigureFaultDetectionParameters)");
            Assert.That(heater!.MaxBadReadings, Is.EqualTo(5),
                        "M570 R sets heat.heaters[0].maxBadReadings (RRF Heater.cpp ConfigureFaultDetectionParameters)");
        });

        string report = await bench.Host.ExecuteCodeAsync("M570 H0");
        Assert.That(report, Does.Contain("15.0").And.Contain("120.0").And.Contain("5"),
                    "the M570 report matches the model's fault detection parameters (RRF Heater.cpp ConfigureFaultDetectionParameters)");
    }
}
