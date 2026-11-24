using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Heat;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using static System.FormattableString;

namespace SystemTests.Scenarios.Codes.Heat;

/// <summary>M950 H, creating, reconfiguring, reporting and deleting a heater</summary>
[TestFixture]
[GCode("M950")]
public class M950HeaterTests : BenchFixture
{
    /// <summary>The heater the scenarios create</summary>
    private const int HeaterNumber = 2;

    /// <summary>The sensor that heater reads</summary>
    private const int SensorNumber = 2;

    /// <summary>A second sensor, for the ambient temperature</summary>
    private const int AmbientSensorNumber = 3;

    /// <summary>The output the heater drives</summary>
    private static readonly string HeaterPort = Expansion.Out(Machines.DriverBoard, 0);

    /// <summary>Sensors 2 and 3 on the driver board, which a heater on that board can read</summary>
    private static readonly string Sensors = $"""
        M308 S{SensorNumber} P"{Expansion.Temp(Machines.DriverBoard, 0)}" Y"thermistor" T100000 B4138
        M308 S{AmbientSensorNumber} P"{Expansion.Temp(Machines.DriverBoard, 1)}" Y"thermistor" T100000 B4138
        """;

    /// <summary>The sensors and heater 2 reading sensor 2</summary>
    private static readonly string SensorsAndHeater = Sensors + $"\nM950 H{HeaterNumber} C\"{HeaterPort}\" T{SensorNumber}";

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
    /// returns ABS_ZERO (-273.15). heat.heaters[].port, .frequency and .ambientSensor are a documented
    /// DSF addition (rrf-differences.md section 3, the object model must recreate the machine)
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950CreatesHeaterBoundToSensor()
    {
        await using Bench bench = await Bench.StartAsync();
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
            Assert.That(heater!.AmbientSensor, Is.EqualTo(-1),
                        "with no B heat.heaters[3].ambientSensor is -1 (DSF addition, rrf-differences.md section 3)");
        });
    }

    /// <summary>
    /// The heater's board is sent every parameter of the heater, defaults included, with the board
    /// address taken off the port
    /// </summary>
    /// <remarks>
    /// RemoteHeater::ConfigurePortAndSensor (RemoteHeater.cpp) adds H, Q, T, C and B every time. The
    /// regression references m950-heater-on-expansion and m950-heater-on-6hc record
    /// <c>{B: -1, C: "out0", H: 3, Q: 250, T: 3}</c> and the same with <c>Q: 100</c>
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950SendsTheBoardTheWholeHeater()
    {
        await using Bench bench = await Bench.With(SensorsAndHeater).KeepingConfigTraffic().StartAsync();

        (byte board, CanMessageM950Heater sent) = bench.CanMaster.LastCanMessage<CanMessageM950Heater>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the heater is created on the board its port names");
            Assert.That(sent.H, Is.EqualTo(HeaterNumber), "H is the heater number");
            Assert.That(sent.C, Is.EqualTo("out0"), "C is the pin as the board knows it");
            Assert.That(sent.T, Is.EqualTo(SensorNumber), "T is the sensor the board reads");
            Assert.That(sent.Q, Is.EqualTo(Heater.DefaultFrequency), "Q states DefaultHeaterPwmFreq");
            Assert.That(sent.B, Is.EqualTo(-1), "B states that there is no ambient sensor");
        });
    }

    /// <summary>
    /// M950 H B names the sensor the heater takes as its ambient temperature, which the board is told
    /// and heat.heaters[].ambientSensor records
    /// </summary>
    /// <remarks>
    /// Heat::ConfigureHeater reads B with TryGetIValue (RRF 3.7.0-beta.2). The regression reference
    /// m950-heater-ambient-sensor records <c>{B: 3, C: "out0", H: 2, Q: 250, T: 2}</c>
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950BSetsTheAmbientSensor()
    {
        await using Bench bench = await Bench.StartAsync(Sensors);

        string reply = await bench.Host.ExecuteCodeAsync(
            $"M950 H{HeaterNumber} C\"{HeaterPort}\" T{SensorNumber} B{AmbientSensorNumber}");
        Heater? heater = await bench.Host.ReadModelAsync(model => model.Heat.Heaters[HeaterNumber]);
        (_, CanMessageM950Heater sent) = bench.CanMaster.LastCanMessage<CanMessageM950Heater>();
        Assert.Multiple(() =>
        {
            Assert.That(reply.Trim(), Is.Empty, "the heater is created without comment");
            Assert.That(sent.B, Is.EqualTo(AmbientSensorNumber), "B reaches the board that runs the heater model");
            Assert.That(heater?.AmbientSensor, Is.EqualTo(AmbientSensorNumber),
                        "and heat.heaters[2].ambientSensor records it (DSF addition)");
            Assert.That(heater?.Sensor, Is.EqualTo(SensorNumber), "the controlling sensor is still T");
        });
    }

    /// <summary>
    /// M950 H Q sets the PWM frequency, and one above MaxHeaterPwmFrequency is clamped to it
    /// </summary>
    /// <remarks>
    /// Heat::ConfigureHeater: <c>min(gb.GetPwmFrequency(), MaxHeaterPwmFrequency)</c>, so the heater's
    /// MOSFET is not switched faster than it can stand
    /// </remarks>
    [TestCase(100, 100)]
    [TestCase(5000, 1000)]
    [Category("KnownGap")]
    public async Task M950QSetsTheHeaterFrequency(int requested, int expected)
    {
        await using Bench bench = await Bench.StartAsync(Sensors);

        await bench.Host.ExecuteCodeAsync($"M950 H{HeaterNumber} C\"{HeaterPort}\" T{SensorNumber} Q{requested}");
        (_, CanMessageM950Heater sent) = bench.CanMaster.LastCanMessage<CanMessageM950Heater>();
        Assert.Multiple(async () =>
        {
            Assert.That(sent.Q, Is.EqualTo(expected), "the board is sent the frequency, at most MaxHeaterPwmFrequency");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Heat.Heaters[HeaterNumber]!.Frequency),
                        Is.EqualTo(expected), "and heat.heaters[2].frequency records the same figure");
        });
    }

    /// <summary>
    /// M950 H with a port and no T is refused before anything is touched, so a heater that held the
    /// number keeps its pin
    /// </summary>
    /// <remarks>
    /// Heat::ConfigureHeater does <c>gb.MustSee('T')</c> before it deletes the old heater. The
    /// regression reference error-missing-parameters-heaters records
    /// <c>Error: M950: missing parameter 'T'</c> and no CAN traffic
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950WithoutTIsRefusedAndKeepsTheHeater()
    {
        await using Bench bench = await Bench.StartAsync(SensorsAndHeater);

        string reply = await bench.Host.ExecuteCodeAsync($"M950 H{HeaterNumber} C\"{Expansion.Out(Machines.DriverBoard, 1)}\"");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:").And.Contain("missing parameter 'T'"),
                        "a heater needs the sensor it reads (Heat.cpp ConfigureHeater, MustSee)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Heat.Heaters[HeaterNumber]?.Port),
                        Is.EqualTo(HeaterPort), "heater 2 is untouched");
            Assert.That(bench.CanMaster.CanMessages<CanMessageM950Heater>(), Is.Empty, "and no board was told anything");
        });
    }

    /// <summary>
    /// M950 H C"nil" deletes a heater: it leaves heat.heaters[] and its board is told to release the
    /// pin, and what the board says to that is not the code's result
    /// </summary>
    /// <remarks>
    /// Heat::ConfigureHeater deletes the heater, and <c>~RemoteHeater</c> sends the board H and
    /// C"nil" only, ignoring the reply. The regression reference m950-heater-delete records exactly
    /// that, with the expansion board answering <c>Missing sensor number</c> (result 4) and the code
    /// still succeeding; reporting the heater afterwards is <c>Error: Heater 2 does not exist</c>
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950DeletesAHeaterWithCNil()
    {
        await using Bench bench = await Bench.StartAsync(SensorsAndHeater);
        bench.CanMaster.AnswerWith<CanMessageM950Heater>(message => message.T is null && message.C is not null
            ? (CodeResult.Error, "Missing sensor number")
            : (CodeResult.Ok, string.Empty));

        string deleted = await bench.Host.ExecuteCodeAsync($"M950 H{HeaterNumber} C\"nil\"");
        (byte board, CanMessageM950Heater released) = bench.CanMaster.LastCanMessage<CanMessageM950Heater>();
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(deleted.Trim(), Is.Empty, "deleting is not refused by what the board says about it");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Heat.Heaters.ElementAtOrDefault(HeaterNumber)),
                        Is.Null, "heater 2 leaves heat.heaters[]");
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the release goes to the heater's board");
            Assert.That(released.H, Is.EqualTo(HeaterNumber));
            Assert.That(released.C, Is.EqualTo("nil"), "naming nil, which frees the pin there");
            Assert.That(released.T, Is.Null, "and nothing else (RemoteHeater destructor)");
            Assert.That(released.Q, Is.Null);
        });

        bench.CanMaster.ClearCapture();
        string report = await bench.Host.ExecuteCodeAsync($"M950 H{HeaterNumber}");
        Assert.Multiple(() =>
        {
            Assert.That(report, Does.StartWith("Error:").And.Contain($"Heater {HeaterNumber} does not exist"),
                        "a deleted heater cannot be reported (Heat.cpp ConfigureHeater)");
            Assert.That(bench.CanMaster.CanMessages<CanMessageM950Heater>(), Is.Empty, "and no board is asked about it");
        });
    }

    /// <summary>
    /// M950 H with a port over an existing heater deletes the old one first, releasing its pin, and
    /// then creates the new one
    /// </summary>
    /// <remarks>Heat::ConfigureHeater calls DeleteObject on the old heater before it creates the new</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950ReplacesAHeaterReleasingTheOldPinFirst()
    {
        await using Bench bench = await Bench.StartAsync(SensorsAndHeater);

        string newPort = Expansion.Out(Machines.DriverBoard, 1);
        await bench.Host.ExecuteCodeAsync($"M950 H{HeaterNumber} C\"{newPort}\" T{SensorNumber}");
        var sent = bench.CanMaster.CanMessages<CanMessageM950Heater>();
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(sent.Select(m => m.Message.C), Is.EqualTo(new[] { "nil", "out1" }),
                        "the old pin is released before the new one is claimed");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Heat.Heaters[HeaterNumber]!.Port),
                        Is.EqualTo(newPort), "and heat.heaters[2].port is the new port");
        });
    }

    /// <summary>
    /// A board that refuses the port leaves the heater number empty, rather than a heater in the
    /// object model that no board drives
    /// </summary>
    /// <remarks>
    /// Heat::ConfigureHeater deletes the new heater when ConfigurePortAndSensor fails, and the old one
    /// was deleted before it
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task ARefusedHeaterPortLeavesNoHeater()
    {
        await using Bench bench = await Bench.StartAsync(SensorsAndHeater);
        bench.CanMaster.AnswerWith<CanMessageM950Heater>(message => message.C == "out6"
            ? (CodeResult.Error, "Pin 1.out6 is not free")
            : (CodeResult.Ok, string.Empty));

        string reply = await bench.Host.ExecuteCodeAsync(
            $"M950 H{HeaterNumber} C\"{Expansion.Out(Machines.DriverBoard, 6)}\" T{SensorNumber}");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:").And.Contain("is not free"), "the board's refusal is the result");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Heat.Heaters.ElementAtOrDefault(HeaterNumber)),
                        Is.Null, "and heater 2 is gone: the old one was deleted before the new one was refused");
        });
    }

    /// <summary>
    /// A board that warns about the heater, such as about a sensor it does not have, still creates it
    /// </summary>
    /// <remarks>
    /// LocalHeater::ConfigurePortAndSensor on the board answers a sensor it cannot find with the
    /// warning <c>Sensor number N has not been defined</c> and keeps the heater, and
    /// Heat::ConfigureHeater keeps a heater whose result Succeeded
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task ABoardWarningAboutTheSensorStillCreatesTheHeater()
    {
        await using Bench bench = await Bench.StartAsync();
        bench.CanMaster.ReportWith<CanMessageM950Heater>("Sensor number 5 has not been defined", CodeResult.Warning);

        string reply = await bench.Host.ExecuteCodeAsync($"M950 H{HeaterNumber} C\"{HeaterPort}\" T5");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply, Does.StartWith("Warning:").And.Contain("has not been defined"),
                        "the board's warning is the result");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Heat.Heaters[HeaterNumber]?.Sensor),
                        Is.EqualTo(5), "and the heater exists, bound to the sensor it was given");
        });
    }

    /// <summary>
    /// M950 H with no other parameter asks the heater's board to report it, and answers with what the
    /// board says
    /// </summary>
    /// <remarks>
    /// RemoteHeater::ReportDetails sends H alone. The regression reference m950-heater-ambient-sensor
    /// records the board's answer <c>Heater 2 pin(s) 3.out0 frequency 250Hz, sensor 2, ambient sensor 3</c>
    /// as the code's reply
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950AsksTheBoardToReportTheHeater()
    {
        const string report = "Heater 2 pin(s) 1.out0 frequency 250Hz, sensor 2, no ambient sensor";
        await using Bench bench = await Bench.StartAsync(SensorsAndHeater);
        bench.CanMaster.ReportWith<CanMessageM950Heater>(report);

        string reply = await bench.Host.ExecuteCodeAsync($"M950 H{HeaterNumber}");
        (byte board, CanMessageM950Heater query) = bench.CanMaster.LastCanMessage<CanMessageM950Heater>();
        Assert.Multiple(() =>
        {
            Assert.That(reply.Trim(), Is.EqualTo(report), "the board's report is the reply");
            Assert.That(board, Is.EqualTo(Machines.DriverBoard));
            Assert.That(query.H, Is.EqualTo(HeaterNumber), "the query names the heater");
            Assert.That(query.C, Is.Null, "and nothing else");
            Assert.That(query.T, Is.Null);
            Assert.That(query.Q, Is.Null);
            Assert.That(query.B, Is.Null);
        });
    }

    /// <summary>
    /// M950 H with T or B and no port is refused: the sensor of an existing heater cannot be changed
    /// </summary>
    /// <remarks>Heat::ConfigureHeater: <c>Can't change sensor number of existing heater</c></remarks>
    [TestCase("T3")]
    [TestCase("B3")]
    [Category("KnownGap")]
    public async Task M950RefusesToChangeTheSensorOfAnExistingHeater(string parameter)
    {
        await using Bench bench = await Bench.StartAsync(SensorsAndHeater);

        string reply = await bench.Host.ExecuteCodeAsync($"M950 H{HeaterNumber} {parameter}");
        Heater? heater = await bench.Host.ReadModelAsync(model => model.Heat.Heaters[HeaterNumber]);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:").And.Contain("Can't change sensor number"),
                        "the sensor is fixed when the heater is created (Heat.cpp ConfigureHeater)");
            Assert.That(heater?.Sensor, Is.EqualTo(SensorNumber), "heat.heaters[2].sensor is unchanged");
            Assert.That(heater?.AmbientSensor, Is.EqualTo(-1), "as is heat.heaters[2].ambientSensor");
            Assert.That(bench.CanMaster.CanMessages<CanMessageM950Heater>(), Is.Empty, "and no board was told anything");
        });
    }

    /// <summary>
    /// M950 H Q with no port changes the frequency of an existing heater on its board, and records it
    /// only once the board has taken it
    /// </summary>
    /// <remarks>RemoteHeater::SetPwmFrequency sends H and Q</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950QChangesTheFrequencyOfAnExistingHeater()
    {
        await using Bench bench = await Bench.StartAsync(SensorsAndHeater);

        await bench.Host.ExecuteCodeAsync($"M950 H{HeaterNumber} Q100");
        (_, CanMessageM950Heater sent) = bench.CanMaster.LastCanMessage<CanMessageM950Heater>();
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(sent.H, Is.EqualTo(HeaterNumber));
            Assert.That(sent.Q, Is.EqualTo(100), "Q is the new frequency");
            Assert.That(sent.C, Is.Null, "and the port is left alone");
            Assert.That(sent.T, Is.Null);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Heat.Heaters[HeaterNumber]!.Frequency),
                        Is.EqualTo(100), "heat.heaters[2].frequency is the new frequency");
        });

        bench.CanMaster.ReportWith<CanMessageM950Heater>("Frequency out of range", CodeResult.Error);
        string refused = await bench.Host.ExecuteCodeAsync($"M950 H{HeaterNumber} Q200");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(refused, Does.StartWith("Error:"), "the board's refusal is the result");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Heat.Heaters[HeaterNumber]!.Frequency),
                        Is.EqualTo(100), "and heat.heaters[2].frequency keeps what the board still has");
        });
    }

    /// <summary>
    /// M950 H past the number of heaters is refused before the port is looked at
    /// </summary>
    /// <remarks>Heat::ConfigureHeater: <c>GetLimitedUIValue('H', MaxHeaters)</c></remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950RefusesAHeaterNumberPastTheLimit()
    {
        await using Bench bench = await Bench.StartAsync(Sensors);

        string reply = await bench.Host.ExecuteCodeAsync(Invariant($"M950 H{HeatManager.MaxHeaters} C\"{HeaterPort}\" T{SensorNumber}"));
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:").And.Contain("parameter 'H' too high"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageM950Heater>(), Is.Empty, "and no pin was claimed");
        });
    }

    /// <summary>
    /// The port name is reduced as RepRapFirmware reads it, so case, underscores and hyphens do not
    /// count, and the reduced form is what is recorded and sent
    /// </summary>
    /// <remarks>Heat::ConfigureHeater reads C with GetReducedString</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950ReducesTheHeaterPortName()
    {
        await using Bench bench = await Bench.StartAsync(Sensors);

        await bench.Host.ExecuteCodeAsync($"M950 H{HeaterNumber} C\"{Machines.DriverBoard}.OUT_0\" T{SensorNumber}");
        (_, CanMessageM950Heater sent) = bench.CanMaster.LastCanMessage<CanMessageM950Heater>();
        Assert.Multiple(async () =>
        {
            Assert.That(sent.C, Is.EqualTo("out0"), "the board is sent the reduced name");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Heat.Heaters[HeaterNumber]!.Port),
                        Is.EqualTo(HeaterPort), "and heat.heaters[2].port records it");
        });
    }
}
