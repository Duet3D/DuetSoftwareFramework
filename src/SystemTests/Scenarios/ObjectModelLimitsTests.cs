using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Model;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios;

/// <summary>
/// <para>
/// The <c>limits</c> key, which says what the machine is built to rather than what it is configured
/// with. RepRapFirmware fills it from compile-time constants and never updates it (RepRap.cpp, "no
/// need for 'limits' because it never changes"), so a running machine must publish every one of them
/// from the moment it starts.
/// </para>
/// <para>
/// A client sizes itself against these, and the codes that refuse an out-of-range device number read
/// the same numbers, so a limit left unset is not a cosmetic gap: <c>M106 H</c> reading a null
/// <c>limits.sensors</c> would accept any sensor number at all
/// </para>
/// </summary>
[TestFixture]
public class ObjectModelLimitsTests : SystemTests.Host.BenchFixture
{
    /// <summary>
    /// The two limits this architecture cannot state, so the completeness check has to skip them
    /// </summary>
    /// <remarks>
    /// Both come from main board hardware in RepRapFirmware, which DSF does not have: every driver is
    /// on an expansion board, so the total is bounded by how many boards are attached rather than by
    /// anything in this build, and the volumes are whatever the SBC has mounted when it is asked
    /// </remarks>
    private static readonly string[] DeliberatelyUnknown = [nameof(Limits.Drivers), nameof(Limits.Volumes)];

    /// <summary>Every limit the object model declares</summary>
    private static IEnumerable<PropertyInfo> LimitProperties
        => typeof(Limits).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(property => property.PropertyType == typeof(int?));

    /// <summary>
    /// Every limit is published, so a client never has to guess one and nothing enforcing a limit
    /// reads a null
    /// </summary>
    /// <remarks>
    /// Driven off the model's own properties rather than a list written out here, so a limit added to
    /// <c>DuetAPI</c> fails this until <c>ObjectModel.SetLimits</c> gives it a value
    /// </remarks>
    [Test]
    public async Task EveryLimitIsPublished()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        Limits limits = await bench.Host.ReadModelAsync(model => model.Limits);
        Assert.Multiple(() =>
        {
            foreach (PropertyInfo property in LimitProperties)
            {
                if (DeliberatelyUnknown.Contains(property.Name))
                {
                    Assert.That(property.GetValue(limits), Is.Null,
                                $"limits.{property.Name} has no figure this side can state, so it stays unknown");
                }
                else
                {
                    Assert.That(property.GetValue(limits), Is.Not.Null,
                                $"limits.{property.Name} is published (DuetControlServer Model/ObjectModel.cs SetLimits)");
                }
            }
        });
    }

    /// <summary>
    /// Each limit holds the figure its owner defines, so the number a client reads is the one the
    /// code enforcing it uses
    /// </summary>
    /// <remarks>
    /// The point of the check is that the two agree, not what either is: a limit restated in the
    /// object model could drift from the constant that bounds the device numbers M950 accepts, and
    /// the machine would then advertise room it refuses to use. The values are named here through the
    /// same constants, so a deliberate change to one moves both and only a divergence fails
    /// </remarks>
    [Test]
    public async Task EveryLimitMatchesTheConstantThatOwnsIt()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        Limits limits = await bench.Host.ReadModelAsync(model => model.Limits);
        Assert.Multiple(() =>
        {
            // Shared with the expansion boards, so the CAN message schema owns them: a bitmap on the
            // bus is what bounds each, and both sides have to agree on the width
            Assert.That(limits.Fans, Is.EqualTo(CanLimits.MaxFans), "limits.fans is CANlib's MaxFans");
            Assert.That(limits.GpInPorts, Is.EqualTo(CanLimits.MaxGpInPorts), "limits.gpInPorts is CANlib's MaxGpInPorts");
            Assert.That(limits.GpOutPorts, Is.EqualTo(CanLimits.MaxGpOutPorts), "limits.gpOutPorts is CANlib's MaxGpOutPorts");
            Assert.That(limits.Heaters, Is.EqualTo(CanLimits.MaxHeaters), "limits.heaters is CANlib's MaxHeaters");
            Assert.That(limits.LedStrips, Is.EqualTo(CanLimits.MaxLedStrips), "limits.ledStrips is CANlib's MaxLedStrips");
            Assert.That(limits.MonitorsPerHeater, Is.EqualTo(CanLimits.MaxMonitorsPerHeater),
                        "limits.monitorsPerHeater is CANlib's MaxMonitorsPerHeater");
            Assert.That(limits.Sensors, Is.EqualTo(CanLimits.MaxSensors), "limits.sensors is CANlib's MaxSensors");
            Assert.That(limits.Spindles, Is.EqualTo(CanLimits.MaxSpindles), "limits.spindles is CANlib's MaxSpindles");
            Assert.That(limits.ZProbeProgramBytes, Is.EqualTo(CanLimits.MaxZProbeProgramBytes),
                        "limits.zProbeProgramBytes is CANlib's MaxZProbeProgramBytes");
            Assert.That(limits.ZProbes, Is.EqualTo(CanLimits.MaxZProbes), "limits.zProbes is CANlib's MaxZProbes");

            // A board is addressed by its CAN address, so the address space is how many there can be
            Assert.That(limits.Boards, Is.EqualTo(CanId.MaxCanAddress + 1),
                        "limits.boards is the CAN address space, DSF at address 0 included");

            // Owned by whichever subsystem enforces them
            Assert.That(limits.Axes, Is.EqualTo(DuetControlServer.Motion.Native.MotionLimits.MaxAxes),
                        "limits.axes is the motion engine's MaxAxes");
            Assert.That(limits.AxesPlusExtruders, Is.EqualTo(DuetControlServer.Motion.Native.MotionLimits.MaxAxesPlusExtruders),
                        "limits.axesPlusExtruders is the motion engine's MaxAxesPlusExtruders");
            Assert.That(limits.DriversPerAxis, Is.EqualTo(DuetControlServer.Motion.Native.MotionLimits.MaxDriversPerAxis),
                        "limits.driversPerAxis is the motion engine's MaxDriversPerAxis");
            Assert.That(limits.Extruders, Is.EqualTo(DuetControlServer.Motion.Native.MotionLimits.MaxExtruders),
                        "limits.extruders is the motion engine's MaxExtruders");
            Assert.That(limits.RestorePoints, Is.EqualTo(DuetControlServer.Motion.RestorePoint.NumVisible),
                        "limits.restorePoints is the number a client can see");
            Assert.That(limits.BedHeaters, Is.EqualTo(DuetControlServer.Heat.HeatManager.MaxBedHeaters),
                        "limits.bedHeaters is the heat subsystem's MaxBedHeaters");
            Assert.That(limits.ChamberHeaters, Is.EqualTo(DuetControlServer.Heat.HeatManager.MaxChamberHeaters),
                        "limits.chamberHeaters is the heat subsystem's MaxChamberHeaters");
            Assert.That(limits.HeatersPerTool, Is.EqualTo(DuetControlServer.Heat.HeatManager.MaxHeatersPerTool),
                        "limits.heatersPerTool is the heat subsystem's MaxHeatersPerTool");
            Assert.That(limits.PortsPerHeater, Is.EqualTo(DuetControlServer.Heat.HeatManager.MaxPortsPerHeater),
                        "limits.portsPerHeater is the heat subsystem's MaxPortsPerHeater");
            Assert.That(limits.Tools, Is.EqualTo(DuetControlServer.Tools.ToolManager.MaxTools),
                        "limits.tools is the tool subsystem's MaxTools");
            Assert.That(limits.ExtrudersPerTool, Is.EqualTo(DuetControlServer.Tools.ToolManager.MaxExtrudersPerTool),
                        "limits.extrudersPerTool is the tool subsystem's MaxExtrudersPerTool");

            // Nothing enforces these yet, so they sit with the rest of what this build is put together
            // with rather than with a subsystem
            Assert.That(limits.ReportedAxes, Is.EqualTo(MachineLimits.MaxReportedAxes),
                        "limits.reportedAxes is MachineLimits.MaxReportedAxes");
            Assert.That(limits.TrackedObjects, Is.EqualTo(MachineLimits.MaxTrackedObjects),
                        "limits.trackedObjects is MachineLimits.MaxTrackedObjects");
            Assert.That(limits.Triggers, Is.EqualTo(MachineLimits.MaxTriggers),
                        "limits.triggers is MachineLimits.MaxTriggers");
            Assert.That(limits.Workplaces, Is.EqualTo(MachineLimits.NumWorkplaces),
                        "limits.workplaces is G54 to G59.3");
        });
    }

    /// <summary>
    /// The limits a code enforces are the ones the model publishes, so a device number the model
    /// says is in range is one M950 accepts
    /// </summary>
    /// <remarks>
    /// The managers hold the same constants the model does, and this is what makes them one number
    /// rather than two that happen to agree today
    /// </remarks>
    [Test]
    public async Task DeviceLimitsMatchWhatTheCodesEnforce()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        Limits limits = await bench.Host.ReadModelAsync(model => model.Limits);

        string tooHigh = await bench.Host.ExecuteCodeAsync($"M950 F{limits.Fans} C\"1.out3\"");
        Assert.That(tooHigh, Does.Contain($"between 0 and {limits.Fans - 1}"),
                    "M950 refuses a fan number at limits.fans and says what the range is");

        string highest = await bench.Host.ExecuteCodeAsync($"M950 F{limits.Fans - 1} C\"1.out3\"");
        Assert.That(highest.Trim(), Is.Empty, "M950 accepts the highest fan number limits.fans allows");

        tooHigh = await bench.Host.ExecuteCodeAsync($"M950 P{limits.GpOutPorts} C\"1.out4\"");
        Assert.That(tooHigh, Does.Contain($"between 0 and {limits.GpOutPorts - 1}"),
                    "M950 refuses an output number at limits.gpOutPorts");

        highest = await bench.Host.ExecuteCodeAsync($"M950 P{limits.GpOutPorts - 1} C\"1.out4\"");
        Assert.That(highest.Trim(), Is.Empty, "M950 accepts the highest output number limits.gpOutPorts allows");
    }
}
