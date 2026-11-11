using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Heat;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace SystemTests.Scenarios.Behaviour.JobControl;

/// <summary>
/// Heating driven through the link: the fake plays the board whose reports are the only source of
/// heater state and temperature, and the temperature waits interact with the job lifecycle
/// </summary>
[TestFixture]
[GCode("M25", "M116")]
public class PauseDuringTemperatureWaitTests : BenchFixture
{
    /// <summary>A bed heater on board 1: sensor 0 reads the board's thermistor, heater 0 drives its output</summary>
    private static readonly string HeatedBed = $"""
        M308 S0 P"{Expansion.Temp(Machines.DriverBoard, 0)}" Y"thermistor"
        M950 H0 C"{Expansion.Out(Machines.DriverBoard, 0)}" T0
        M140 H0
        """;

    /// <summary>
    /// The move after M116 is 7 mm where the one before is 5, so the capture says which side of
    /// the wait the machine is on
    /// </summary>
    private const string HeatAndWaitJob = """
        G91
        G1 X5 F1200
        M140 S60
        M116
        G1 X7 F1200
        """;

    /// <summary>
    /// M25 interrupts a blocking M116, as it does in RepRapFirmware: the pause cuts the wait short
    /// rather than sitting out the heating, and the resume replays the M116 so the temperatures are
    /// waited for again before the job carries on
    /// </summary>
    [Test]
    public async Task PauseInterruptsTemperatureWait()
    {
        await using Bench bench = await Bench.With(Machines.X, HeatedBed, Machines.X.Homed)
                                             .WithSd(sd => sd.WriteGCode("job.gcode", HeatAndWaitJob))
                                             .StartAsync();
        DcsTestHost host = bench.Host;
        ScriptedCanMaster canMaster = bench.CanMaster;

        // The board reports its heater running but cold; wait for the report to land in the model
        // before the job starts, so the M116 finds a heater worth waiting for
        canMaster.InjectHeatersStatus(srcAddress: Machines.DriverBoard, heaterNumber: 0, HeaterMode.Heating, currentTemperature: 25.0f);
        await canMaster.WaitUntilAsync(() => ReadHeater(host) is { Current: > 20.0f and < 30.0f },
                                  what: "the heater report reaching the model");

        HeatManager heat = host.Services.GetRequiredService<HeatManager>();
        await host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");

        // The job runs its first move and then blocks in M116, because the bed never gets warmer
        await canMaster.WaitUntilAsync(() => heat.IsWaitingForTemperatures, what: "M116 blocking on the cold bed");
        Assert.That(canMaster.MoveDistances(), Does.Not.Contain(7.0f), "the move after M116 must not have run");

        // The pause cuts the wait short instead of sitting out the heating
        await host.ExecuteCodeAsync("M25");
        await host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.That(heat.IsWaitingForTemperatures, Is.False, "the pause released the temperature wait");
        Assert.That(canMaster.MoveDistances(), Does.Not.Contain(7.0f), "pausing must not run the move after M116");
#pragma warning disable CS0618
        Assert.That(await host.ReadModelAsync(model => model.State.RestorePoints[1].Coords[0]), Is.EqualTo(5.0).Within(0.01),
                    "with the queue empty the feedhold had nothing to stop: the pause landed between codes at the completed move's target");
#pragma warning restore

        // The resume replays the M116, so the job waits for the bed again
        await host.ExecuteCodeAsync("M24");
        await canMaster.WaitUntilAsync(() => heat.IsWaitingForTemperatures, what: "the replayed M116 blocking again");
        await host.WaitForStatusAsync(MachineStatus.Processing);
        Assert.That(canMaster.MoveDistances(), Does.Not.Contain(7.0f), "resuming alone must not satisfy the wait");

        // The bed arrives; the wait completes and the job runs to its end
        canMaster.InjectHeatersStatus(srcAddress: Machines.DriverBoard, heaterNumber: 0, HeaterMode.Stable, currentTemperature: 60.0f);
        await canMaster.WaitUntilAsync(() => canMaster.MoveDistances().Contains(7.0f), what: "the move after M116 running");
        await host.WaitForStatusAsync(MachineStatus.Idle);
    }

    private static Heater? ReadHeater(DcsTestHost host)
    {
        // A read without the model lock, which is fine for a test poll: the values are floats the
        // dispatcher publishes and the poll re-reads until they settle
        return host.Model.Heat.Heaters.Count > 0 ? host.Model.Heat.Heaters[0] : null;
    }
}
