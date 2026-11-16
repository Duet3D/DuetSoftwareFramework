using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer;
using DuetControlServer.Heat;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using UnitTests.Utility;

namespace UnitTests.Heaters;

/// <summary>
/// The cold extrusion interlock against RepRapFirmware's Heat (M302's configuration), Tool::ExtruderMovementAllowed,
/// Tool::CanDriveExtruder and Tool::DisplayColdExtrusionWarnings
/// </summary>
[TestFixture]
public class ColdExtrusionInterlockTests
{
    /// <summary>The object model and the interlock reading it</summary>
    private sealed record Machine(DuetControlServer.Model.ObjectModel Model, ColdExtrusionInterlock Interlock);

    /// <summary>A machine with heater 1 at the given temperature and tool 0 driving extruder 0, heated by heater 1</summary>
    private static Machine NewMachine(float temperature, out Tool tool)
    {
        StoppedLifetime lifetime = new();
        DuetControlServer.Model.ObjectModel model = new(lifetime, NullLogger<DuetControlServer.Model.ObjectModel>.Instance,
                                                        Options.Create(new Settings()));
        model.Heat.Heaters.Add(null);
        model.Heat.Heaters.Add(new Heater { Current = temperature });
        tool = new Tool { Number = 0 };
        tool.Extruders.Add(0);
        tool.Heaters.Add(1);
        model.Tools.Add(tool);

        HeatManager heatManager = new(model, null!, null!, NullLogger<HeatManager>.Instance);
        DuetControlServer.Utility.EventLogger eventLogger = new(null!, model, lifetime,
                                                                NullLogger<DuetControlServer.Utility.EventLogger>.Instance,
                                                                Options.Create(new Settings()));
        return new Machine(model, new ColdExtrusionInterlock(model, heatManager, eventLogger));
    }

    [Test]
    public void AllowingReportsBothMinimumsAsZeroAndForbiddingBringsThemBack()
    {
        Machine machine = NewMachine(25.0f, out _);
        machine.Interlock.SetMinExtrusionTemperature(120.0f);
        machine.Interlock.SetAllowed(true);
        machine.Interlock.SetMinRetractionTemperature(110.0f);

        Assert.Multiple(() =>
        {
            Assert.That(machine.Model.Heat.ColdExtrudeTemperature, Is.Zero);
            Assert.That(machine.Model.Heat.ColdRetractTemperature, Is.Zero, "R while allowed does not forbid it again");
            Assert.That(machine.Model.Heat.MinExtrusionTemperature, Is.EqualTo(120.0f));
            Assert.That(machine.Model.Heat.MinRetractionTemperature, Is.EqualTo(110.0f));
        });

        machine.Interlock.SetAllowed(false);
        Assert.Multiple(() =>
        {
            Assert.That(machine.Model.Heat.ColdExtrudeTemperature, Is.EqualTo(120.0f));
            Assert.That(machine.Model.Heat.ColdRetractTemperature, Is.EqualTo(110.0f));
        });
    }

    [TestCase(160.0f, true, true)]
    [TestCase(159.9f, true, false)]
    [TestCase(90.0f, false, true)]
    [TestCase(89.9f, false, false)]
    public void AToolIsDrivenFromTheMinimumForTheDirection(float temperature, bool forwards, bool driven)
    {
        Machine machine = NewMachine(temperature, out Tool tool);
        Assert.That(machine.Interlock.CanDriveExtruder(tool, forwards), Is.EqualTo(driven));
    }

    [Test]
    public void AHeaterFaultStopsTheToolHoweverHotItIs()
    {
        Machine machine = NewMachine(200.0f, out Tool tool);
        machine.Model.Heat.Heaters[1]!.State = HeaterState.Fault;
        Assert.That(machine.Interlock.CanDriveExtruder(tool, forwards: true), Is.False);
    }

    [Test]
    public void AHeaterThatDoesNotExistReadsAsAbsoluteZero()
    {
        Machine machine = NewMachine(200.0f, out Tool tool);
        tool.Heaters.Add(5);
        Assert.That(machine.Interlock.CanDriveExtruder(tool, forwards: false), Is.False);
    }

    [Test]
    public void AToolWithoutHeatersIsNotProtected()
    {
        Machine machine = NewMachine(25.0f, out Tool tool);
        tool.Heaters.Clear();
        Assert.That(machine.Interlock.ExtruderMovementAllowed(tool, forwards: true, extruder: 0), Is.True);
    }

    [Test]
    public void AllowingColdExtrusionDrivesAnyExtruder()
    {
        Machine machine = NewMachine(25.0f, out _);
        machine.Interlock.SetAllowed(true);
        Assert.Multiple(() =>
        {
            Assert.That(machine.Interlock.ExtruderMovementAllowed(null, forwards: true, extruder: 0), Is.True, "with no tool");
            Assert.That(machine.Interlock.ExtruderMovementAllowed(machine.Model.Tools[0], forwards: true, extruder: 3), Is.True,
                        "on an extruder that is not the tool's");
        });
    }

    [Test]
    public void ForbiddenColdExtrusionDrivesNoExtruderWithoutTheTool()
    {
        Machine machine = NewMachine(200.0f, out Tool tool);
        Assert.Multiple(() =>
        {
            Assert.That(machine.Interlock.ExtruderMovementAllowed(null, forwards: true, extruder: 0), Is.False, "with no tool");
            Assert.That(machine.Interlock.ExtruderMovementAllowed(tool, forwards: true, extruder: 1), Is.False,
                        "on an extruder that is not the tool's");
            Assert.That(machine.Interlock.ExtruderMovementAllowed(tool, forwards: true, extruder: 0), Is.True, "on the hot tool's own");
        });
    }

    [Test]
    public async Task AToolThatWasNotDrivenIsWarnedAbout()
    {
        Machine machine = NewMachine(25.0f, out Tool tool);
        Assert.That(machine.Interlock.ExtruderMovementAllowed(tool, forwards: true, extruder: 0), Is.False);

        for (int attempt = 0; attempt < 100; attempt++)
        {
            using (await machine.Model.AccessReadOnlyAsync())
            {
                if (machine.Model.Messages.Any())
                {
                    break;
                }
            }
            await Task.Delay(10);
        }

        using (await machine.Model.AccessReadOnlyAsync())
        {
            Assert.That(machine.Model.Messages.Select(message => (message.Type, message.Content)), Is.EqualTo(new[]
            {
                (MessageType.Warning, "Tool 0 was not driven because its heater temperatures were not high enough or it has a heater fault")
            }));
        }
    }
}
