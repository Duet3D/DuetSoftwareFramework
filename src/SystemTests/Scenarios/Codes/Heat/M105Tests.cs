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

/// <summary>M105, the temperature report</summary>
[TestFixture]
[GCode("M105")]
public class M105Tests : BenchFixture
{
    /// <summary>One decimal place, invariant culture, the format RRF reports temperatures in</summary>
    private static string OneDecimal(float value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>
    /// M105 reports every tool and the bed in the Marlin-compatible format, and the numbers are
    /// the model's current and target temperatures
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes::GenerateTemperatureReport and ReportToolTemperatures (GCodes.cpp,
    /// GCodes5.cpp): each tool as "Tn:current /target", the bed as "B:current /target", one
    /// decimal place. The target is the active temperature for an active heater and 0.0 for one
    /// that is off (Heat.cpp GetTargetTemperature)
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M105ReportsTemperaturesConsistentWithModel()
    {
        await using Bench bench = await Bench.StartAsync(HeatConfig);
        await bench.Host.ExecuteCodeAsync("M140 S60");

        bench.CanMaster.InjectHeatersStatus(srcAddress: Machines.DriverBoard, heaterNumber: 0, HeaterMode.Heating, currentTemperature: 28.5f);
        bench.CanMaster.InjectHeatersStatus(srcAddress: Machines.DriverBoard, heaterNumber: 1, HeaterMode.Off, currentTemperature: 25.0f);
        await bench.CanMaster.WaitUntilAsync(
            () => ReadHeater(bench.Host, 0) is { Current: > 28.0f and < 29.0f }
                  && ReadHeater(bench.Host, 1) is { Current: > 24.0f and < 26.0f },
            what: "both heater reports reaching the model");

        Heater bed = ReadHeater(bench.Host, 0)!;
        Heater toolHeater = ReadHeater(bench.Host, 1)!;
        string reply = await bench.Host.ExecuteCodeAsync("M105");

        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Contain($"T0:{OneDecimal(toolHeater.Current)} /0.0"),
                        "M105 reports the tool heater's current and a 0.0 target while it is off (RRF GCodes5.cpp ReportToolTemperatures, Heat.cpp GetTargetTemperature)");
            Assert.That(reply, Does.Contain($"B:{OneDecimal(bed.Current)} /{OneDecimal(bed.Active)}"),
                        "M105 reports the bed's current and active temperatures (RRF GCodes.cpp GenerateTemperatureReport)");
        });
    }
}
