using System.Globalization;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Heat;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Codes;

/// <summary>
/// The heating M-codes and their object model effects, asserted against RepRapFirmware's handlers
/// in GCodes2.cpp and the Heat/Heater object model tables. The fake board is the only source of
/// heater temperatures, so every current reading and heater mode in these tests is injected;
/// blocking waits (M109, M116, M190, M191) are satisfied by injecting the target temperature
/// </summary>
[TestFixture]
public class HeatCodeTests : SystemTests.Host.BenchFixture
{
    /// <summary>
    /// A full heating bench on board 1: sensor and heater 0 form the bed (M140 H0), sensor and
    /// heater 1 belong to tool 0 (M563), sensor and heater 2 form the chamber (M141 H2)
    /// </summary>
    private const string HeatConfig = """
        M308 S0 P"1.temp0" Y"thermistor"
        M950 H0 C"1.out0" T0
        M140 H0
        M308 S1 P"1.temp1" Y"thermistor"
        M950 H1 C"1.out1" T1
        M563 P0 D0 H1
        M308 S2 P"1.temp2" Y"thermistor"
        M950 H2 C"1.out2" T2
        M141 H2
        """;

    /// <summary>Lock-free model read of one heater, in the polling shape HeatingTests uses</summary>
    private static Heater? ReadHeater(DcsTestHost host, int heater)
        => host.Model.Heat.Heaters.Count > heater ? host.Model.Heat.Heaters[heater] : null;

    /// <summary>One decimal place, invariant culture, the format RRF reports temperatures in</summary>
    private static string OneDecimal(float value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>
    /// M308 creates a sensor: sensors.analog[] gains an entry whose name is the A parameter, whose
    /// type is the Y parameter, and whose lastReading follows the board's sensor reports
    /// </summary>
    /// <remarks>
    /// RRF truth: Heat::ConfigureSensor (Heat.cpp) creates the sensor from S/P/Y and the sensor's
    /// Configure applies A; the reported fields are TemperatureSensor::objectModelTable
    /// (TemperatureSensor.cpp): name, type, lastReading
    /// </remarks>
    [Test]
    public async Task M308CreatesSensorInObjectModel()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: HeatConfig);
        await bench.Host.ExecuteCodeAsync("M308 S3 P\"1.temp3\" Y\"thermistor\" A\"probe temp\"");

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.EvaluateRawAsync("sensors.analog[3].name"), Is.EqualTo("probe temp"),
                        "M308 A sets sensors.analog[3].name (RRF TemperatureSensor.cpp sensorName)");
            Assert.That(await bench.Host.EvaluateRawAsync("sensors.analog[3].type"), Is.EqualTo("thermistor"),
                        "M308 Y sets sensors.analog[3].type (RRF Thermistor.h GetShortSensorType)");
        });

        bench.CanMaster.InjectSensorReport(srcAddress: 1, sensorNumber: 3, temperature: 23.5f);
        await bench.CanMaster.WaitUntilAsync(
            () => bench.Host.Model.Sensors.Analog.Count > 3
                  && bench.Host.Model.Sensors.Analog[3]?.LastReading is > 23.0f and < 24.0f,
            what: "the sensor report reaching sensors.analog[3].lastReading");
        Assert.That(await bench.Host.EvaluateAsync("sensors.analog[3].lastReading"), Is.EqualTo(23.5).Within(0.01),
                    "sensors.analog[3].lastReading follows the board's report (RRF TemperatureSensor.cpp lastTemperature)");
    }

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
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: HeatConfig);
        await bench.Host.ExecuteCodeAsync("M308 S3 P\"1.temp3\" Y\"thermistor\"");
        await bench.Host.ExecuteCodeAsync("M950 H3 C\"1.out3\" T3");

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.EvaluateAsync("heat.heaters[3].sensor"), Is.EqualTo(3),
                        "M950 T binds heat.heaters[3].sensor (RRF Heat.cpp ConfigureHeater)");
            Assert.That(await bench.Host.EvaluateRawAsync("heat.heaters[3].state"), Is.EqualTo("off"),
                        "a new heater starts off (RRF Heater.cpp GetStatus)");
            Assert.That(await bench.Host.EvaluateRawAsync("heat.heaters[3].model.enabled"), Is.EqualTo("false"),
                        "M950 H alone leaves heat.heaters[3].model.enabled false until a function is assigned (RRF FOPDT.cpp)");
            Assert.That(ReadHeater(bench.Host, 3)?.Monitors, Has.Count.EqualTo(3),
                        "a heater carries MaxMonitorsPerHeater monitor slots (RRF Heater.cpp objectModelTable monitors[])");
            Assert.That(ReadHeater(bench.Host, 3)?.Monitors.Count > 0
                            ? ReadHeater(bench.Host, 3)!.Monitors[0]?.Condition
                            : null,
                        Is.EqualTo(HeaterMonitorCondition.Disabled),
                        "a new heater's monitors are all disabled (RRF HeaterMonitor.cpp GetTriggerName)");
            Assert.That(await bench.Host.EvaluateAsync("heat.heaters[3].max"), Is.EqualTo(2000).Within(0.01),
                        "with no monitor heat.heaters[3].max is BadErrorTemperature (RRF Heater.cpp GetHighestTemperatureLimit)");
            Assert.That(await bench.Host.EvaluateAsync("heat.heaters[3].min"), Is.EqualTo(-273.15).Within(0.01),
                        "with no monitor heat.heaters[3].min is ABS_ZERO (RRF Heater.cpp GetLowestTemperatureLimit)");
            Assert.That(await bench.Host.EvaluateRawAsync("heat.heaters[3].port"), Is.EqualTo("1.out3"),
                        "M950 C is kept in heat.heaters[3].port (DSF addition, rrf-differences.md section 3)");
            Assert.That(await bench.Host.EvaluateAsync("heat.heaters[3].frequency"), Is.EqualTo(250),
                        "heat.heaters[3].frequency defaults to DefaultHeaterPwmFreq (DSF addition, rrf-differences.md section 3)");
        });
    }

    /// <summary>
    /// M140: H0 in config.g made heater 0 the bed with the default bed monitor, S sets the active
    /// temperature and switches the heater to active, R sets the standby temperature, and a
    /// temperature at or below absolute zero switches the heater off without touching the setpoint
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp case 140. H assigns via Heat::SetBedHeaters, which triggers
    /// Heater::SetFunction and the default bed monitor at DefaultBedTemperatureLimit (125). S calls
    /// SetActiveTemperature plus SetActiveOrStandby(active), R calls SetStandbyTemperature only,
    /// and S below NEARLY_ABS_ZERO calls SwitchOff. heat.bedHeaters[] is Heat.cpp's obsolete
    /// bed heaters array, reporting the first heater of the slot
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M140AssignsAndDrivesBedHeater()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: HeatConfig);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.EvaluateRawAsync("heat.bedHeaters[0]"), Is.EqualTo("0"),
                        "M140 H0 sets heat.bedHeaters[0] (RRF Heat.cpp bedHeaters array)");
            Assert.That(await bench.Host.EvaluateAsync("heat.heaters[0].max"), Is.EqualTo(125).Within(0.01),
                        "assigning a bed sets the default monitor at DefaultBedTemperatureLimit (RRF Heater.cpp SetFunction)");
            Assert.That(await bench.Host.EvaluateRawAsync("heat.heaters[0].model.enabled"), Is.EqualTo("true"),
                        "assigning a bed enables the default model (RRF Heater.cpp SetFunction)");
        });

        await bench.Host.ExecuteCodeAsync("M140 S60");
        Assert.That(await bench.Host.EvaluateAsync("heat.heaters[0].active"), Is.EqualTo(60).Within(0.01),
                    "M140 S sets heat.heaters[0].active (RRF GCodes2.cpp case 140, SetActiveTemperature)");

        // The heater is commanded active; the board reporting a PID mode makes that visible
        bench.CanMaster.InjectHeatersStatus(srcAddress: 1, heaterNumber: 0, HeaterMode.Heating, currentTemperature: 25.0f);
        await bench.CanMaster.WaitUntilAsync(() => ReadHeater(bench.Host, 0) is { State: HeaterState.Active },
                                             what: "the bed heater reporting active");
        Assert.That(await bench.Host.EvaluateRawAsync("heat.heaters[0].state"), Is.EqualTo("active"),
                    "M140 S switches heat.heaters[0].state to active (RRF Heat.cpp SetActiveOrStandby)");

        await bench.Host.ExecuteCodeAsync("M140 R40");
        Assert.That(await bench.Host.EvaluateAsync("heat.heaters[0].standby"), Is.EqualTo(40).Within(0.01),
                    "M140 R sets heat.heaters[0].standby (RRF GCodes2.cpp case 140, SetStandbyTemperature)");

        await bench.Host.ExecuteCodeAsync("M140 S-274");
        bench.CanMaster.InjectHeatersStatus(srcAddress: 1, heaterNumber: 0, HeaterMode.Off, currentTemperature: 25.0f);
        await bench.CanMaster.WaitUntilAsync(() => ReadHeater(bench.Host, 0) is { State: HeaterState.Off },
                                             what: "the bed heater reporting off");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.EvaluateRawAsync("heat.heaters[0].state"), Is.EqualTo("off"),
                        "M140 S below absolute zero switches the bed off (RRF GCodes2.cpp case 140, SwitchOff)");
            Assert.That(await bench.Host.EvaluateAsync("heat.heaters[0].active"), Is.EqualTo(60).Within(0.01),
                        "switching off leaves heat.heaters[0].active untouched (RRF Heater.cpp SwitchOff)");
        });
    }

    /// <summary>M140 H-1 clears the bed mapping, so the slot reports -1 again</summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp case 140 calls Heat::ClearBedHeaters for H-1, and the obsolete
    /// bedHeaters array reports HeaterCollection::GetFirstHeater, which is -1 for an empty slot
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M140HMinusOneClearsBedMapping()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: HeatConfig);
        await bench.Host.ExecuteCodeAsync("M140 H-1");
        Assert.That(await bench.Host.EvaluateRawAsync("heat.bedHeaters[0]"), Is.EqualTo("-1"),
                    "M140 H-1 clears heat.bedHeaters[0] to -1 (RRF Heat.cpp ClearBedHeaters, HeaterCollection GetFirstHeater)");
    }

    /// <summary>
    /// M141: H2 in config.g made heater 2 the chamber with the default chamber monitor, and S sets
    /// the active temperature and switches the heater to active
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp case 141, sharing the case 140 handler. Assignment triggers
    /// Heater::SetFunction with DefaultChamberTemperatureLimit (100); heat.chamberHeaters[] is
    /// Heat.cpp's obsolete chamber heaters array
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M141AssignsAndDrivesChamberHeater()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: HeatConfig);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.EvaluateRawAsync("heat.chamberHeaters[0]"), Is.EqualTo("2"),
                        "M141 H2 sets heat.chamberHeaters[0] (RRF Heat.cpp chamberHeaters array)");
            Assert.That(await bench.Host.EvaluateAsync("heat.heaters[2].max"), Is.EqualTo(100).Within(0.01),
                        "assigning a chamber sets the default monitor at DefaultChamberTemperatureLimit (RRF Heater.cpp SetFunction)");
        });

        await bench.Host.ExecuteCodeAsync("M141 S50");
        Assert.That(await bench.Host.EvaluateAsync("heat.heaters[2].active"), Is.EqualTo(50).Within(0.01),
                    "M141 S sets heat.heaters[2].active (RRF GCodes2.cpp case 141, SetActiveTemperature)");

        bench.CanMaster.InjectHeatersStatus(srcAddress: 1, heaterNumber: 2, HeaterMode.Heating, currentTemperature: 25.0f);
        await bench.CanMaster.WaitUntilAsync(() => ReadHeater(bench.Host, 2) is { State: HeaterState.Active },
                                             what: "the chamber heater reporting active");
        Assert.That(await bench.Host.EvaluateRawAsync("heat.heaters[2].state"), Is.EqualTo("active"),
                    "M141 S switches heat.heaters[2].state to active (RRF Heat.cpp SetActiveOrStandby)");
    }

    /// <summary>
    /// A bare M104 targets the lowest-numbered tool when none is selected, sets both the active and
    /// the standby temperature of the tool and its heater, puts the tool on standby, and never
    /// selects it
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp case 104. The applicable tool is
    /// MovementState::GetLockedCurrentOrDefaultTool (RawMove.cpp), the current tool else the
    /// lowest-numbered one. SetToolHeaters (GCodes.cpp) sets active and standby alike because
    /// slicers do not know the difference, and a tool that is not the current tool is put on
    /// standby (Tool::Standby). The heater setpoints follow through
    /// Tool::SetToolHeaterActiveOrStandbyTemperature (Tool.cpp). The tool default monitor from
    /// M563 is DefaultHotEndTemperatureLimit (285, Heater.cpp SetFunction)
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M104SetsToolTemperaturesWithoutSelecting()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: HeatConfig);
        Assert.That(await bench.Host.EvaluateAsync("heat.heaters[1].max"), Is.EqualTo(285).Within(0.01),
                    "M563 assigns the tool function and the default monitor at DefaultHotEndTemperatureLimit (RRF Heater.cpp SetFunction)");

        await bench.Host.ExecuteCodeAsync("M104 S200");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.EvaluateAsync("tools[0].active[0]"), Is.EqualTo(200).Within(0.01),
                        "a bare M104 S sets tools[0].active[0] of the default tool (RRF GCodes2.cpp case 104, SetToolHeaters)");
            Assert.That(await bench.Host.EvaluateAsync("tools[0].standby[0]"), Is.EqualTo(200).Within(0.01),
                        "M104 S sets tools[0].standby[0] as well (RRF GCodes.cpp SetToolHeaters)");
            Assert.That(await bench.Host.EvaluateAsync("heat.heaters[1].active"), Is.EqualTo(200).Within(0.01),
                        "M104 S reaches heat.heaters[1].active (RRF Tool.cpp SetToolHeaterActiveOrStandbyTemperature)");
            Assert.That(await bench.Host.EvaluateAsync("heat.heaters[1].standby"), Is.EqualTo(200).Within(0.01),
                        "M104 S reaches heat.heaters[1].standby (RRF Tool.cpp SetToolHeaterActiveOrStandbyTemperature)");
            Assert.That(await bench.Host.EvaluateRawAsync("state.currentTool"), Is.EqualTo("-1"),
                        "M104 never selects a tool (RRF GCodes2.cpp case 104)");
            Assert.That(await bench.Host.EvaluateRawAsync("tools[0].state"), Is.EqualTo("standby"),
                        "M104 puts the unselected target tool on standby (RRF GCodes2.cpp case 104, Tool::Standby)");
        });

        // The tool is on standby, so the heater runs at the standby setpoint: a PID mode from the
        // board must read back as standby, not active
        bench.CanMaster.InjectHeatersStatus(srcAddress: 1, heaterNumber: 1, HeaterMode.Heating, currentTemperature: 25.0f);
        await bench.CanMaster.WaitUntilAsync(() => ReadHeater(bench.Host, 1) is { State: HeaterState.Standby },
                                             what: "the tool heater reporting standby");
        Assert.That(await bench.Host.EvaluateRawAsync("heat.heaters[1].state"), Is.EqualTo("standby"),
                    "M104 on an unselected tool leaves heat.heaters[1].state standby (RRF Heater.cpp GetStatus)");
    }

    /// <summary>
    /// M109 with no tool selected selects the target tool, sets active and standby temperatures,
    /// and blocks until the heater reports the target temperature
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp case 109: same temperature handling as M104, then a tool change to
    /// the applicable tool when none is selected (GCodeState::m109ToolChange0) and a wait in
    /// GCodeState::m109WaitForTemperature until ToolHeatersAtSetTemperatures
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M109SelectsToolAndWaitsForTemperature()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: HeatConfig);
        HeatManager heat = bench.Host.Services.GetRequiredService<HeatManager>();

        // The board reports the tool heater cold before the wait starts, so there is something to wait for
        bench.CanMaster.InjectHeatersStatus(srcAddress: 1, heaterNumber: 1, HeaterMode.Heating, currentTemperature: 25.0f);
        await bench.CanMaster.WaitUntilAsync(() => ReadHeater(bench.Host, 1) is { Current: > 20.0f and < 30.0f },
                                             what: "the tool heater report reaching the model");

        Task<string> waitTask = bench.Host.ExecuteCodeAsync("M109 S210");
        await bench.CanMaster.WaitUntilAsync(() => heat.IsWaitingForTemperatures || waitTask.IsCompleted,
                                             what: "M109 either blocking on the cold tool heater or completing");
        bool blocked = !waitTask.IsCompleted && heat.IsWaitingForTemperatures;
        if (blocked)
        {
            Assert.That(ReadHeater(bench.Host, 1), Is.Not.Null.And.Property("Active").EqualTo(210.0f).Within(0.01),
                        "M109 S sets heat.heaters[1].active before waiting (RRF GCodes2.cpp case 109, SetToolHeaters)");
            bench.CanMaster.InjectHeatersStatus(srcAddress: 1, heaterNumber: 1, HeaterMode.Stable, currentTemperature: 210.0f);
        }
        string reply = await waitTask;

        Assert.Multiple(async () =>
        {
            Assert.That(blocked, Is.True,
                        $"M109 blocks until the tool heater reaches temperature (RRF GCodes2.cpp case 109, m109WaitForTemperature); reply was: {reply}");
            Assert.That(await bench.Host.EvaluateRawAsync("state.currentTool"), Is.EqualTo("0"),
                        "M109 with no tool selected selects the target tool (RRF GCodes2.cpp case 109, m109ToolChange0)");
            Assert.That(await bench.Host.EvaluateRawAsync("tools[0].state"), Is.EqualTo("active"),
                        "the tool M109 selected is active (RRF MovementState::SelectTool)");
            Assert.That(await bench.Host.EvaluateAsync("tools[0].active[0]"), Is.EqualTo(210).Within(0.01),
                        "M109 S sets tools[0].active[0] (RRF GCodes.cpp SetToolHeaters)");
            Assert.That(await bench.Host.EvaluateAsync("tools[0].standby[0]"), Is.EqualTo(210).Within(0.01),
                        "M109 S sets tools[0].standby[0] as well (RRF GCodes.cpp SetToolHeaters)");
            Assert.That(await bench.Host.EvaluateRawAsync("heat.heaters[1].state"), Is.EqualTo("active"),
                        "the selected tool's heater is active (RRF Heater.cpp GetStatus)");
            Assert.That(await bench.Host.EvaluateAsync("heat.heaters[1].current"), Is.EqualTo(210).Within(0.01),
                        "heat.heaters[1].current follows the board's report");
        });
    }

    /// <summary>M190 sets the bed's active temperature, switches it on, and blocks until it is reached</summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp case 190: SetActiveTemperature plus SetActiveOrStandby(active) for
    /// each bed heater of the slot, then HeaterAtSetTemperature until the wait is satisfied
    /// </remarks>
    [Test]
    public async Task M190SetsBedTemperatureAndWaits()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: HeatConfig);
        HeatManager heat = bench.Host.Services.GetRequiredService<HeatManager>();

        bench.CanMaster.InjectHeatersStatus(srcAddress: 1, heaterNumber: 0, HeaterMode.Heating, currentTemperature: 25.0f);
        await bench.CanMaster.WaitUntilAsync(() => ReadHeater(bench.Host, 0) is { Current: > 20.0f and < 30.0f },
                                             what: "the bed heater report reaching the model");

        Task<string> waitTask = bench.Host.ExecuteCodeAsync("M190 S60");
        await bench.CanMaster.WaitUntilAsync(() => heat.IsWaitingForTemperatures, what: "M190 blocking on the cold bed");
        Assert.Multiple(() =>
        {
            Assert.That(waitTask.IsCompleted, Is.False, "M190 blocks until the bed reaches temperature (RRF HeaterAtSetTemperature)");
            Assert.That(ReadHeater(bench.Host, 0), Is.Not.Null.And.Property("Active").EqualTo(60.0f).Within(0.01),
                        "M190 S sets heat.heaters[0].active before waiting (RRF GCodes2.cpp case 190, SetActiveTemperature)");
        });

        bench.CanMaster.InjectHeatersStatus(srcAddress: 1, heaterNumber: 0, HeaterMode.Stable, currentTemperature: 60.0f);
        await waitTask;

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.EvaluateRawAsync("heat.heaters[0].state"), Is.EqualTo("active"),
                        "M190 switches the bed active (RRF GCodes2.cpp case 190, SetActiveOrStandby)");
            Assert.That(await bench.Host.EvaluateAsync("heat.heaters[0].current"), Is.EqualTo(60).Within(0.01),
                        "heat.heaters[0].current follows the board's report");
        });
    }

    /// <summary>M191 sets the chamber's active temperature, switches it on, and blocks until it is reached</summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp case 191, sharing the case 190 handler over the chamber heaters of
    /// the slot
    /// </remarks>
    [Test]
    public async Task M191SetsChamberTemperatureAndWaits()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: HeatConfig);
        HeatManager heat = bench.Host.Services.GetRequiredService<HeatManager>();

        bench.CanMaster.InjectHeatersStatus(srcAddress: 1, heaterNumber: 2, HeaterMode.Heating, currentTemperature: 25.0f);
        await bench.CanMaster.WaitUntilAsync(() => ReadHeater(bench.Host, 2) is { Current: > 20.0f and < 30.0f },
                                             what: "the chamber heater report reaching the model");

        Task<string> waitTask = bench.Host.ExecuteCodeAsync("M191 S50");
        await bench.CanMaster.WaitUntilAsync(() => heat.IsWaitingForTemperatures, what: "M191 blocking on the cold chamber");
        Assert.Multiple(() =>
        {
            Assert.That(waitTask.IsCompleted, Is.False, "M191 blocks until the chamber reaches temperature (RRF HeaterAtSetTemperature)");
            Assert.That(ReadHeater(bench.Host, 2), Is.Not.Null.And.Property("Active").EqualTo(50.0f).Within(0.01),
                        "M191 S sets heat.heaters[2].active before waiting (RRF GCodes2.cpp case 191, SetActiveTemperature)");
        });

        bench.CanMaster.InjectHeatersStatus(srcAddress: 1, heaterNumber: 2, HeaterMode.Stable, currentTemperature: 50.0f);
        await waitTask;

        Assert.That(await bench.Host.EvaluateRawAsync("heat.heaters[2].state"), Is.EqualTo("active"),
                    "M191 switches the chamber active (RRF GCodes2.cpp case 191, SetActiveOrStandby)");
    }

    /// <summary>
    /// M116 with no parameters blocks until every heater with a setpoint is at temperature; the
    /// bed set by M140 counts, and the wait releases when the board reports the target
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp case 116: with no parameters it waits for the current tool's
    /// heaters, every bed heater and every chamber heater via HeaterAtSetTemperature with
    /// tolerance TemperatureCloseEnough
    /// </remarks>
    [Test]
    public async Task M116WaitsForOutstandingTemperatures()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: HeatConfig);
        HeatManager heat = bench.Host.Services.GetRequiredService<HeatManager>();

        bench.CanMaster.InjectHeatersStatus(srcAddress: 1, heaterNumber: 0, HeaterMode.Heating, currentTemperature: 25.0f);
        await bench.CanMaster.WaitUntilAsync(() => ReadHeater(bench.Host, 0) is { Current: > 20.0f and < 30.0f },
                                             what: "the bed heater report reaching the model");
        await bench.Host.ExecuteCodeAsync("M140 S60");

        Task<string> waitTask = bench.Host.ExecuteCodeAsync("M116");
        await bench.CanMaster.WaitUntilAsync(() => heat.IsWaitingForTemperatures, what: "M116 blocking on the cold bed");
        Assert.That(waitTask.IsCompleted, Is.False, "M116 blocks while the bed is below its setpoint (RRF GCodes2.cpp case 116)");

        bench.CanMaster.InjectHeatersStatus(srcAddress: 1, heaterNumber: 0, HeaterMode.Stable, currentTemperature: 60.0f);
        await waitTask;
        Assert.That(await bench.Host.EvaluateAsync("heat.heaters[0].current"), Is.EqualTo(60).Within(0.01),
                    "the wait released at the reported target temperature");
    }

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
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: HeatConfig);
        await bench.Host.ExecuteCodeAsync("M140 S60");

        bench.CanMaster.InjectHeatersStatus(srcAddress: 1, heaterNumber: 0, HeaterMode.Heating, currentTemperature: 28.5f);
        bench.CanMaster.InjectHeatersStatus(srcAddress: 1, heaterNumber: 1, HeaterMode.Off, currentTemperature: 25.0f);
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

    /// <summary>
    /// M302 controls cold extrusion: allowed reports both minimum temperatures as 0, forbidding
    /// restores the defaults, and S/R set the minimums the model reports
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp case 302 and Heat.cpp's objectModelTable: coldExtrudeTemperature
    /// and coldRetractTemperature report 0 while cold extrusion is allowed, else the configured
    /// minimums, whose defaults are DefaultMinExtrusionTemperature (160) and
    /// DefaultMinRetractionTemperature (90) from RRF3Common.h. The bench config allows cold
    /// extrusion (M302 P1 in JobControlBench.XyeConfig)
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M302ControlsColdExtrusion()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: HeatConfig);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.EvaluateAsync("heat.coldExtrudeTemperature"), Is.EqualTo(0).Within(0.01),
                        "with M302 P1 heat.coldExtrudeTemperature reports 0 (RRF Heat.cpp objectModelTable)");
            Assert.That(await bench.Host.EvaluateAsync("heat.coldRetractTemperature"), Is.EqualTo(0).Within(0.01),
                        "with M302 P1 heat.coldRetractTemperature reports 0 (RRF Heat.cpp objectModelTable)");
        });

        await bench.Host.ExecuteCodeAsync("M302 P0");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.EvaluateAsync("heat.coldExtrudeTemperature"), Is.EqualTo(160).Within(0.01),
                        "M302 P0 restores heat.coldExtrudeTemperature to DefaultMinExtrusionTemperature (RRF GCodes2.cpp case 302)");
            Assert.That(await bench.Host.EvaluateAsync("heat.coldRetractTemperature"), Is.EqualTo(90).Within(0.01),
                        "M302 P0 restores heat.coldRetractTemperature to DefaultMinRetractionTemperature (RRF GCodes2.cpp case 302)");
        });

        await bench.Host.ExecuteCodeAsync("M302 S120 R110");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.EvaluateAsync("heat.coldExtrudeTemperature"), Is.EqualTo(120).Within(0.01),
                        "M302 S sets heat.coldExtrudeTemperature (RRF Heat.cpp SetExtrusionMinTemp)");
            Assert.That(await bench.Host.EvaluateAsync("heat.coldRetractTemperature"), Is.EqualTo(110).Within(0.01),
                        "M302 R sets heat.coldRetractTemperature (RRF Heat.cpp SetRetractionMinTemp)");
        });

        string report = await bench.Host.ExecuteCodeAsync("M302");
        Assert.That(report, Does.Contain("forbidden").And.Contain("120.0").And.Contain("110.0"),
                    "the M302 report matches the model's minimum temperatures (RRF GCodes2.cpp case 302)");
    }

    /// <summary>M307 sets the heater process model fields the object model reports</summary>
    /// <remarks>
    /// RRF truth: Heater::SetOrReportModel (Heater.cpp) parses R/K/E/D/S/V/B and FopDt's
    /// objectModelTable (FOPDT.cpp) reports heatingRate, coolingRate, fanCoolingRate, coolingExp,
    /// deadTime, maxPwm, standardVoltage, and pid.used from usePid
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M307SetsHeaterProcessModel()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: HeatConfig);
        await bench.Host.ExecuteCodeAsync("M307 H0 R2.8 K0.35:0.11 E1.4 D6.5 S0.9 V23.5 B0");

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.EvaluateAsync("heat.heaters[0].model.heatingRate"), Is.EqualTo(2.8).Within(0.001),
                        "M307 R sets heat.heaters[0].model.heatingRate (RRF Heater.cpp SetOrReportModel)");
            Assert.That(await bench.Host.EvaluateAsync("heat.heaters[0].model.coolingRate"), Is.EqualTo(0.35).Within(0.001),
                        "M307 K sets heat.heaters[0].model.coolingRate (RRF FOPDT.cpp basicCoolingRate)");
            Assert.That(await bench.Host.EvaluateAsync("heat.heaters[0].model.fanCoolingRate"), Is.EqualTo(0.11).Within(0.001),
                        "M307 K's second value sets heat.heaters[0].model.fanCoolingRate (RRF FOPDT.cpp fanCoolingRate)");
            Assert.That(await bench.Host.EvaluateAsync("heat.heaters[0].model.coolingExp"), Is.EqualTo(1.4).Within(0.001),
                        "M307 E sets heat.heaters[0].model.coolingExp (RRF FOPDT.cpp coolingRateExponent)");
            Assert.That(await bench.Host.EvaluateAsync("heat.heaters[0].model.deadTime"), Is.EqualTo(6.5).Within(0.001),
                        "M307 D sets heat.heaters[0].model.deadTime (RRF Heater.cpp SetOrReportModel)");
            Assert.That(await bench.Host.EvaluateAsync("heat.heaters[0].model.maxPwm"), Is.EqualTo(0.9).Within(0.001),
                        "M307 S sets heat.heaters[0].model.maxPwm (RRF Heater.cpp SetOrReportModel)");
            Assert.That(await bench.Host.EvaluateAsync("heat.heaters[0].model.standardVoltage"), Is.EqualTo(23.5).Within(0.001),
                        "M307 V sets heat.heaters[0].model.standardVoltage (RRF Heater.cpp SetOrReportModel)");
            Assert.That(await bench.Host.EvaluateRawAsync("heat.heaters[0].model.pid.used"), Is.EqualTo("true"),
                        "M307 B0 keeps PID in use (RRF FOPDT.cpp pid.used)");
            Assert.That(await bench.Host.EvaluateRawAsync("heat.heaters[0].model.enabled"), Is.EqualTo("true"),
                        "a configured model reports enabled (RRF FOPDT.cpp enabled)");
            Assert.That(await bench.Host.EvaluateRawAsync("heat.heaters[0].model.inverted"), Is.EqualTo("false"),
                        "without I the model is not inverted (RRF FOPDT.cpp inverted)");
        });

        await bench.Host.ExecuteCodeAsync("M307 H0 B1");
        Assert.That(await bench.Host.EvaluateRawAsync("heat.heaters[0].model.pid.used"), Is.EqualTo("false"),
                    "M307 B1 switches to bang-bang, so heat.heaters[0].model.pid.used is false (RRF Heater.cpp SetOrReportModel)");
    }

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
    [Category("KnownGap")]
    [Test]
    public async Task M143ConfiguresHeaterMonitors()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: HeatConfig);

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
            Assert.That(await bench.Host.EvaluateAsync("heat.heaters[0].max"), Is.EqualTo(110).Within(0.01),
                        "heat.heaters[0].max reports the tooHigh monitor limit (RRF Heater.cpp GetHighestTemperatureLimit)");
            Assert.That(monitor1?.Limit, Is.EqualTo(5.0f).Within(0.01),
                        "M143 P1 S sets heat.heaters[0].monitors[1].limit (RRF Heater.cpp ConfigureMonitor)");
            Assert.That(monitor1?.Condition, Is.EqualTo(HeaterMonitorCondition.TooLow),
                        "M143 C1 selects the TemperatureTooLow trigger (RRF HeaterMonitor.cpp GetTriggerName)");
            Assert.That(monitor1?.Action, Is.EqualTo(HeaterMonitorAction.TemporarySwitchOff),
                        "M143 A2 selects the TemporarySwitchOff action (RRF Heater.cpp ConfigureMonitor)");
            Assert.That(monitor1?.Sensor, Is.EqualTo(0),
                        "M143 T names the sensor the monitor watches (RRF Heater.cpp ConfigureMonitor)");
            Assert.That(await bench.Host.EvaluateAsync("heat.heaters[0].min"), Is.EqualTo(5).Within(0.01),
                        "heat.heaters[0].min reports the tooLow monitor limit (RRF Heater.cpp GetLowestTemperatureLimit)");
        });
    }

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
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: HeatConfig);
        await bench.Host.ExecuteCodeAsync("M570 H0 P120 T15 R5");

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.EvaluateAsync("heat.heaters[0].maxHeatingFaultTime"), Is.EqualTo(120).Within(0.01),
                        "M570 P sets heat.heaters[0].maxHeatingFaultTime (RRF Heater.cpp ConfigureFaultDetectionParameters)");
            Assert.That(await bench.Host.EvaluateAsync("heat.heaters[0].maxTempExcursion"), Is.EqualTo(15).Within(0.01),
                        "M570 T sets heat.heaters[0].maxTempExcursion (RRF Heater.cpp ConfigureFaultDetectionParameters)");
            Assert.That(await bench.Host.EvaluateAsync("heat.heaters[0].maxBadReadings"), Is.EqualTo(5),
                        "M570 R sets heat.heaters[0].maxBadReadings (RRF Heater.cpp ConfigureFaultDetectionParameters)");
        });

        string report = await bench.Host.ExecuteCodeAsync("M570 H0");
        Assert.That(report, Does.Contain("15.0").And.Contain("120.0").And.Contain("5"),
                    "the M570 report matches the model's fault detection parameters (RRF Heater.cpp ConfigureFaultDetectionParameters)");
    }

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
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: HeatConfig);

        bench.CanMaster.InjectHeatersStatus(srcAddress: 1, heaterNumber: 0, HeaterMode.Fault, currentTemperature: 25.0f);
        await bench.CanMaster.WaitUntilAsync(() => ReadHeater(bench.Host, 0) is { State: HeaterState.Fault },
                                             what: "the fault report reaching the model");
        Assert.That(await bench.Host.EvaluateRawAsync("heat.heaters[0].state"), Is.EqualTo("fault"),
                    "a fault report sets heat.heaters[0].state to fault (RRF Heater.cpp GetStatus)");

        string reply = await bench.Host.ExecuteCodeAsync("M562 P0");
        Assert.That(reply, Does.Not.Contain("Error"),
                    "M562 P accepts the fault reset (RRF GCodes2.cpp case 562, Tool::ClearTemperatureFault)");

        // The board acknowledges the reset by reporting the heater off again
        bench.CanMaster.InjectHeatersStatus(srcAddress: 1, heaterNumber: 0, HeaterMode.Off, currentTemperature: 25.0f);
        await bench.CanMaster.WaitUntilAsync(() => ReadHeater(bench.Host, 0) is { State: HeaterState.Off },
                                             what: "the heater leaving the fault state");
        Assert.That(await bench.Host.EvaluateRawAsync("heat.heaters[0].state"), Is.EqualTo("off"),
                    "after M562 the heater's state follows the board's report again (RRF RemoteHeater.cpp ResetFault)");
    }
}
