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

/// <summary>M562, clearing a heater fault</summary>
[TestFixture]
[GCode("M562")]
public class M562Tests : BenchFixture
{
    /// <summary>
    /// M562 clears a heater fault: the board reported the fault, M562 commands the reset, and the
    /// heater leaves the fault state with the board's next report
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp case 562 calls Tool::ClearTemperatureFault, which resets the
    /// heater's fault (Heat::ResetFault) and every tool's fault flag. For a remote heater the
    /// reset is a CAN command (RemoteHeater::ResetFault) and heat.heaters[].state leaves fault
    /// when the board next reports a non-fault mode
    /// </remarks>
    [Test]
    public async Task M562ClearsHeaterFault()
    {
        await using Bench bench = await Bench.StartAsync(HeatConfig);

        bench.CanMaster.InjectHeatersStatus(srcAddress: Machines.DriverBoard, heaterNumber: 0, HeaterMode.Fault, currentTemperature: 25.0f);
        await bench.CanMaster.WaitUntilAsync(() => ReadHeater(bench.Host, 0) is { State: HeaterState.Fault },
                                             what: "the fault report reaching the model");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Heat.Heaters[0]!.State), Is.EqualTo(HeaterState.Fault),
                    "a fault report sets heat.heaters[0].state to fault (RRF Heater.cpp GetStatus)");

        string reply = await bench.Host.ExecuteCodeAsync("M562 P0");
        Assert.That(reply, Does.Not.Contain("Error"),
                    "M562 P accepts the fault reset (RRF GCodes2.cpp case 562, Tool::ClearTemperatureFault)");

        // The board acknowledges the reset by reporting the heater off again
        bench.CanMaster.InjectHeatersStatus(srcAddress: Machines.DriverBoard, heaterNumber: 0, HeaterMode.Off, currentTemperature: 25.0f);
        await bench.CanMaster.WaitUntilAsync(() => ReadHeater(bench.Host, 0) is { State: HeaterState.Off },
                                             what: "the heater leaving the fault state");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Heat.Heaters[0]!.State), Is.EqualTo(HeaterState.Off),
                    "after M562 the heater's state follows the board's report again (RRF RemoteHeater.cpp ResetFault)");
    }
}
