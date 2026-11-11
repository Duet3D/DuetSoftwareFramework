using System;
using System.Globalization;
using System.Threading.Tasks;
using DuetControlServer.Heat;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Tools.ToolSetup;

namespace SystemTests.Scenarios.Codes.Tools;

/// <summary>M568, a tool's temperatures and heater states</summary>
[TestFixture]
[GCode("M568")]
public class M568Tests : BenchFixture
{
    /// <summary>Poll an object model field until it reaches the expected value</summary>
    private static async Task WaitForValueAsync<T>(DcsTestHost host,
                                                   Func<DuetControlServer.Model.ObjectModel, T> read,
                                                   T expected, string what, int timeoutMs = 10_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        T value;
        do
        {
            value = await host.ReadModelAsync(read);
            if (Equals(value, expected))
            {
                return;
            }
            await Task.Delay(25);
        }
        while (DateTime.UtcNow < deadline);
        Assert.Fail($"{what}: reads '{value}', expected '{expected}'");
    }

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
        await WaitForValueAsync(host, model => model.Heat.Heaters[0]!.State, HeaterState.Active,
                               "M568 A2 puts the heater in active (RRF Tool::HeatersToActiveOrStandby, Heater::GetStatus)");

        await host.ExecuteCodeAsync("M568 P0 A1");
        await WaitForValueAsync(host, model => model.Heat.Heaters[0]!.State, HeaterState.Standby,
                               "M568 A1 puts the heater in standby (RRF Tool::HeatersToActiveOrStandby, Heater::GetStatus)");

        await host.ExecuteCodeAsync("M568 P0 A0");
        bench.CanMaster.InjectHeatersStatus(srcAddress: Machines.DriverBoard, heaterNumber: 0, HeaterMode.Off, currentTemperature: 30.0f);
        await WaitForValueAsync(host, model => model.Heat.Heaters[0]!.State, HeaterState.Off,
                               "M568 A0 switches the heater off (RRF Tool::HeatersToOff, Heater::GetStatus)");

        string reply = (await host.ExecuteCodeAsync("M568 P0")).Trim();
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Tool 0"), "bare M568 reports the tool (RRF SetOrReportOffsets)");
            Assert.That(reply, Does.Contain("Tool 0 is Off, active temperatures: 210.0, standby temperatures: 180.0"), "bare M568 reports the model temperatures (RRF SetOrReportOffsets)");
        });
    }
}
