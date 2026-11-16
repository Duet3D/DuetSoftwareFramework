using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using static System.FormattableString;
using static SystemTests.Scenarios.Codes.Heat.HeatSetup;

namespace SystemTests.Scenarios.Codes.Heat;

/// <summary>M308, the temperature sensors</summary>
/// <remarks>
/// RRF truth is Heat::ConfigureSensor (Heat.cpp), RemoteSensor (RemoteSensor.cpp) and
/// TemperatureSensor (TemperatureSensor.cpp); the regression cases are the m308-* ones in
/// <c>testcases/heaters</c>, which use sensor 2 on b0
/// </remarks>
[TestFixture]
[GCode("M308")]
public class M308Tests : BenchFixture
{
    /// <summary>The sensor number the regression cases configure</summary>
    private const int SensorNumber = 2;

    /// <summary>A second expansion board the bench answers for, to move a sensor to</summary>
    private const byte OtherBoard = DriversRig.ClosedLoopBoard;

    /// <summary>Thermistor coefficients of the regression cases: T, B and R</summary>
    private const float R25 = 100000.0f, Beta = 4138.0f, SeriesResistor = 2200.0f;

    /// <summary>The Steinhart-Hart C of the regression cases, as written in the G-code</summary>
    private const string ShCText = "7.06e-8";

    /// <summary>The thermistor input the sensors are on</summary>
    private static readonly string ThermistorPort = Expansion.Temp(Machines.DriverBoard, 0);

    /// <summary>Sensor 2, a thermistor on the driver board, as the regression cases set it up</summary>
    private static readonly string ThermistorSensor =
        Invariant($"M308 S{SensorNumber} P\"{ThermistorPort}\" Y\"thermistor\" T{R25} B{Beta}");

    /// <summary>A sensor of the object model, or null where sensors.analog[] has no entry for it</summary>
    private static AnalogSensor? SensorAt(DuetControlServer.Model.ObjectModel model, int sensor)
        => sensor < model.Sensors.Analog.Count ? model.Sensors.Analog[sensor] : null;

    /// <summary>Every M308 sent to a board since the capture was last cleared</summary>
    private static IReadOnlyList<(byte Board, CanMessageM308V1 Message)> SentM308(Bench bench)
        => bench.CanMaster.CanMessages<CanMessageM308V1>();

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
        await using Bench bench = await Bench.StartAsync(HeatConfig);
        await bench.Host.ExecuteCodeAsync($"M308 S3 P\"{Expansion.Temp(Machines.DriverBoard, 3)}\" Y\"thermistor\" A\"probe temp\"");

        AnalogSensor? sensor = await bench.Host.ReadModelAsync(model => model.Sensors.Analog[3]);
        Assert.Multiple(() =>
        {
            Assert.NotNull(sensor);
            Assert.That(sensor!.Name, Is.EqualTo("probe temp"),
                        "M308 A sets sensors.analog[3].name (RRF TemperatureSensor.cpp sensorName)");
            Assert.That(sensor!.Type, Is.EqualTo(AnalogSensorType.Thermistor),
                        "M308 Y sets sensors.analog[3].type (RRF Thermistor.h GetShortSensorType)");
        });

        bench.CanMaster.InjectSensorReport(srcAddress: Machines.DriverBoard, sensorNumber: 3, temperature: 23.5f);
        await bench.CanMaster.WaitUntilAsync(
            () => bench.Host.Model.Sensors.Analog.Count > 3
                  && bench.Host.Model.Sensors.Analog[3]?.LastReading is > 23.0f and < 24.0f,
            what: "the sensor report reaching sensors.analog[3].lastReading");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Analog[3]?.LastReading), Is.EqualTo(23.5).Within(0.01),
                    "sensors.analog[3].lastReading follows the board's report (RRF TemperatureSensor.cpp lastTemperature)");
    }

    /// <summary>
    /// M308 Y on an expansion board sends the board the thermistor's coefficients, with the port as
    /// the board knows it, and records them; the new sensor is notReady until the board reports
    /// </summary>
    /// <remarks>
    /// RemoteSensor::Configure sends CanMessageGenericConstructor::PopulateFromCommand of M308V1Params,
    /// which strips the board address from P and carries no A. TemperatureSensor's constructor starts
    /// lastResult at notReady. Regression case m308-thermistor-on-expansion: paramMap 0x00C8F
    /// {S, P "temp0", Y "thermistor", T, B, C, R}
    /// </remarks>
    [Test]
    public async Task M308SendsAThermistorToItsBoardAndRecordsIt()
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync(
            Invariant($"M308 S{SensorNumber} P\"{ThermistorPort}\" Y\"thermistor\" T{R25} B{Beta} C{ShCText} R{SeriesResistor} A\"Test sensor\""));
        float shC = float.Parse(ShCText, CultureInfo.InvariantCulture);

        Assert.That(reply, Does.Not.StartWith("Error:").And.Not.StartWith("Warning:"), "the board took the sensor");
        Assert.That(SentM308(bench), Has.Count.EqualTo(1), "one M308 goes to one board");
        (byte board, CanMessageM308V1 sent) = SentM308(bench)[0];
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the M308 goes to the board named by the port");
            Assert.That(sent.S, Is.EqualTo(SensorNumber), "S is the sensor number");
            Assert.That(sent.P, Is.EqualTo("temp0"), "P is the port without the board address");
            Assert.That(sent.Y, Is.EqualTo("thermistor"), "Y is the sensor type");
            Assert.That(sent.T, Is.EqualTo(R25).Within(1e-3), "T is the resistance at 25C");
            Assert.That(sent.B, Is.EqualTo(Beta).Within(1e-3), "B is the beta");
            Assert.That(sent.C, Is.EqualTo(shC).Within(1e-12), "C is the Steinhart-Hart C");
            Assert.That(sent.R, Is.EqualTo(SeriesResistor).Within(1e-3), "R is the series resistor");
            Assert.That(sent.L, Is.Null, "no L was given");
            Assert.That(sent.H, Is.Null, "no H was given");
            Assert.That(sent.F, Is.Null);
            Assert.That(sent.W, Is.Null);
            Assert.That(sent.K, Is.Null);
            Assert.That(sent.U, Is.Null);
            Assert.That(sent.V, Is.Null);
        });

        AnalogSensor? sensor = await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber));
        Assert.Multiple(() =>
        {
            Assert.That(sensor, Is.Not.Null, "sensors.analog[2] exists");
            Assert.That(sensor?.Port, Is.EqualTo(ThermistorPort), "sensors.analog[2].port keeps the board address");
            Assert.That(sensor?.Name, Is.EqualTo("Test sensor"), "the name stays on this side");
            Assert.That(sensor?.R25, Is.EqualTo(R25).Within(1e-3), "sensors.analog[2].r25 is T");
            Assert.That(sensor?.Beta, Is.EqualTo(Beta).Within(1e-3), "sensors.analog[2].beta is B");
            Assert.That(sensor?.C, Is.EqualTo(shC).Within(1e-12), "sensors.analog[2].c is C");
            Assert.That(sensor?.RRef, Is.EqualTo(SeriesResistor).Within(1e-3), "sensors.analog[2].rRef is R");
            Assert.That(sensor?.OffsetAdj, Is.Zero, "no U, so no offset adjustment");
            Assert.That(sensor?.SlopeAdj, Is.Zero, "no V, so no slope adjustment");
            Assert.That(sensor?.State, Is.EqualTo(TemperatureError.NotReady),
                        "a new sensor is notReady until its board reports (RRF TemperatureSensor constructor)");
        });
    }

    /// <summary>The Y names of the sensor types an expansion board reads, and the port each goes on</summary>
    private static readonly TestCaseData[] SensorTypes =
    [
        new(Expansion.Temp(Machines.DriverBoard, 0), "thermistor", AnalogSensorType.Thermistor, "thermistor"),
        new(Expansion.Temp(Machines.DriverBoard, 0), "pt1000", AnalogSensorType.PT1000, "pt1000"),
        new(Expansion.Temp(Machines.DriverBoard, 0), "linear-analog", AnalogSensorType.LinearAnalog, "linearanalog"),
        new(Expansion.Temp(Machines.DriverBoard, 0), "Linear_Analog", AnalogSensorType.LinearAnalog, "linearanalog"),
        new(Expansion.SpiCs(Machines.DriverBoard, 0), "rtd-max31865", AnalogSensorType.MAX31865, "rtdmax31865"),
        new(Expansion.SpiCs(Machines.DriverBoard, 0), "thermocouple-max31855", AnalogSensorType.MAX31855, "thermocouplemax31855"),
        new(Expansion.SpiCs(Machines.DriverBoard, 0), "thermocouple-max31856", AnalogSensorType.MAX31856, "thermocouplemax31856"),
        new(Expansion.Dummy(Machines.DriverBoard), "mcu-temp", AnalogSensorType.McuTemp, "mcutemp"),
        new(Expansion.Dummy(Machines.DriverBoard), "drivers", AnalogSensorType.Drivers, "drivers"),
        new(Expansion.Dummy(Machines.DriverBoard), "board-temp", AnalogSensorType.BoardTemp, "boardtemp"),
        new(Expansion.HeaterCurrent(Machines.DriverBoard), "current", AnalogSensorType.Current, "current"),
        new(Expansion.Dummy(Machines.DriverBoard), "thermopile_tpis.object", AnalogSensorType.ThermopileObject, "thermopiletpis.object"),
        new(Expansion.SensorOutput(Machines.DriverBoard, 1, 1), "thermopile_tpis.ambient", AnalogSensorType.ThermopileAmbient, "thermopiletpis.ambient"),
        new(Expansion.SensorOutput(Machines.DriverBoard, 1, 2), "thermopile_tpis.environment", AnalogSensorType.ThermopileEnvironment, "thermopiletpis.environment"),
    ];

    /// <summary>
    /// A sensor type name is matched ignoring case, '-' and '_', is sent to the board in that reduced
    /// form, and sets sensors.analog[].type
    /// </summary>
    /// <remarks>
    /// GetReducedString (StringParser.cpp) lowercases and drops '-' and '_' for both Y and P, which is
    /// what CanMessageGenericConstructor sends: regression cases m308-linear-analog ("linearanalog"),
    /// m308-rtd-max31865, m308-thermocouple-max31856, m308-mcu-temp-sensor
    /// and m308-drivers-sensor ("dummy" port). The wiki says case, '-' and '_' are not significant.
    /// RRF itself reports "remote" for every expansion-board sensor (RemoteSensor.h "TODO save the
    /// actual type"); the object model has to say what the sensor is to recreate it
    /// </remarks>
    [TestCaseSource(nameof(SensorTypes))]
    public async Task M308SendsTheReducedTypeNameAndRecordsTheType(string port, string typeName, AnalogSensorType type, string wireType)
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M308 S{SensorNumber} P\"{port}\" Y\"{typeName}\"");

        (byte board, CanMessageM308V1 sent) = bench.CanMaster.LastCanMessage<CanMessageM308V1>();
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply, Does.Not.StartWith("Error:"), "the board took the sensor");
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the M308 goes to the board named by the port");
            Assert.That(sent.Y, Is.EqualTo(wireType), "Y travels as a reduced string");
            Assert.That(sent.P, Is.EqualTo(port[(port.IndexOf('.') + 1)..]), "P travels without the board address");
            Assert.That(await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber)?.Type), Is.EqualTo(type),
                        "sensors.analog[2].type is the type the name stands for");
            Assert.That(await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber)?.Port), Is.EqualTo(port),
                        "sensors.analog[2].port is the port as written");
        });
    }

    /// <summary>
    /// A linear analog sensor's B and C are the readings at the two ends of the ADC range, and F
    /// selects filtering; they reach the board as written and are recorded as the low and high readings
    /// </summary>
    /// <remarks>
    /// LinearAnalogSensor::Configure reads B, C and F; regression case m308-linear-analog sends
    /// {S, P "temp0", Y "linearanalog", F 1, B 0, C 100}. sensors.analog[].lowReading and .highReading
    /// are documented as the linear analog sensor's, and beta and c as the thermistor's
    /// </remarks>
    [Test]
    public async Task M308LinearAnalogRecordsItsRange()
    {
        const float low = 0.0f, high = 100.0f;
        const int filtered = 1;
        await using Bench bench = await Bench.StartAsync();

        await bench.Host.ExecuteCodeAsync(Invariant($"M308 S{SensorNumber} P\"{ThermistorPort}\" Y\"linear-analog\" F{filtered} B{low} C{high}"));

        (_, CanMessageM308V1 sent) = bench.CanMaster.LastCanMessage<CanMessageM308V1>();
        AnalogSensor? sensor = await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber));
        Assert.Multiple(() =>
        {
            Assert.That(sent.F, Is.EqualTo(filtered), "F is sent as written");
            Assert.That(sent.B, Is.EqualTo(low).Within(1e-3), "B is sent as written");
            Assert.That(sent.C, Is.EqualTo(high).Within(1e-3), "C is sent as written");
            Assert.That(sensor?.LowReading, Is.EqualTo(low).Within(1e-3), "B is sensors.analog[2].lowReading");
            Assert.That(sensor?.HighReading, Is.EqualTo(high).Within(1e-3), "C is sensors.analog[2].highReading");
            Assert.That(sensor?.Beta, Is.Null, "a linear analog sensor has no beta");
            Assert.That(sensor?.C, Is.Null, "nor a Steinhart-Hart C");
            Assert.That(sensor?.Filtered, Is.True, "F1 is sensors.analog[2].filtered");
        });
    }

    /// <summary>
    /// A thermopile sensor's environment thermistor curve, its output mixes and its radiation exponent reach the board
    /// as written and are recorded; an exponent the board ignores is not
    /// </summary>
    /// <remarks>
    /// Duet3Expansion's TPiS_1T_1086_L5_5::Configure reads T, B and C for the environment thermistor (B without C
    /// sets C to 0), F and W as the object and auxiliary mixes in 256ths, and R as the radiation exponent only when
    /// it lies between 3.8 and 4.4
    /// </remarks>
    [TestCase(4.0f, 4.0f)]
    [TestCase(5.0f, null)]
    public async Task M308RecordsTheThermopileSettings(float exponent, float? recorded)
    {
        const float r25 = 100000.0f, beta = 4100.0f;
        const int objectMix = 200, auxMix = 56;
        await using Bench bench = await Bench.StartAsync();

        await bench.Host.ExecuteCodeAsync(Invariant(
            $"M308 S{SensorNumber} P\"{Expansion.Dummy(Machines.DriverBoard)}\" Y\"thermopile_tpis.object\" T{r25} B{beta} F{objectMix} W{auxMix} R{exponent}"));

        (_, CanMessageM308V1 sent) = bench.CanMaster.LastCanMessage<CanMessageM308V1>();
        AnalogSensor? sensor = await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber));
        Assert.Multiple(() =>
        {
            Assert.That(sent.T, Is.EqualTo(r25).Within(1e-3), "T is sent as written");
            Assert.That(sent.B, Is.EqualTo(beta).Within(1e-3), "B is sent as written");
            Assert.That(sent.F, Is.EqualTo(objectMix), "F is sent as written");
            Assert.That(sent.W, Is.EqualTo(auxMix), "W is sent as written");
            Assert.That(sent.R, Is.EqualTo(exponent).Within(1e-3), "R is sent whether or not the board takes it");
            Assert.That(sensor?.Type, Is.EqualTo(AnalogSensorType.ThermopileObject));
            Assert.That(sensor?.R25, Is.EqualTo(r25).Within(1e-3), "T is the environment thermistor's r25");
            Assert.That(sensor?.Beta, Is.EqualTo(beta).Within(1e-3), "B is its beta");
            Assert.That(sensor?.C, Is.Zero, "and B without C sets its C to 0");
            Assert.That(sensor?.ObjectMix, Is.EqualTo(objectMix), "F is sensors.analog[].objectMix");
            Assert.That(sensor?.AuxMix, Is.EqualTo(auxMix), "W is sensors.analog[].auxMix");
            Assert.That(sensor?.RadiationExponent, Is.EqualTo(recorded), "R is sensors.analog[].radiationExponent when the board takes it");
        });
    }

    /// <summary>
    /// The SPI sensors' own parameters reach the board as written: an RTD's reference resistor, wire
    /// count and mains frequency, and a thermocouple's type letter and mains frequency
    /// </summary>
    /// <remarks>
    /// Regression cases m308-rtd-max31865 (R400 W2 F50) and m308-thermocouple-max31856 (K"K" F50). K is
    /// a single-character parameter (ParamDescriptor char_p)
    /// </remarks>
    [Test]
    public async Task M308SendsTheSpiSensorParameters()
    {
        const float rtdReference = 400.0f;
        const int wires = 2, mainsFrequency = 50;
        const char thermocoupleType = 'K';
        await using Bench bench = await Bench.StartAsync();

        await bench.Host.ExecuteCodeAsync(Invariant(
            $"M308 S2 P\"{Expansion.SpiCs(Machines.DriverBoard, 1)}\" Y\"rtd-max31865\" R{rtdReference} W{wires} F{mainsFrequency}"));
        await bench.Host.ExecuteCodeAsync(Invariant(
            $"M308 S3 P\"{Expansion.SpiCs(Machines.DriverBoard, 0)}\" Y\"thermocouple-max31856\" K\"{thermocoupleType}\" F{mainsFrequency}"));

        CanMessageM308V1 Sent(int sensor) => SentM308(bench).Single(sent => sent.Message.S == sensor).Message;
        CanMessageM308V1 rtd = Sent(2), thermocouple = Sent(3);
        Assert.Multiple(() =>
        {
            Assert.That(rtd.P, Is.EqualTo("spi.cs1"), "an SPI sensor's port is its chip select");
            Assert.That(rtd.R, Is.EqualTo(rtdReference).Within(1e-3), "an RTD's R is its reference resistor");
            Assert.That(rtd.W, Is.EqualTo(wires), "W is the number of wires");
            Assert.That(rtd.F, Is.EqualTo(mainsFrequency), "F is the mains frequency");
            Assert.That(thermocouple.K, Is.EqualTo(thermocoupleType), "K is the thermocouple type letter");
            Assert.That(thermocouple.F, Is.EqualTo(mainsFrequency), "F is the mains frequency");
        });

        AnalogSensor? rtdSensor = await bench.Host.ReadModelAsync(model => SensorAt(model, 2));
        AnalogSensor? thermocoupleSensor = await bench.Host.ReadModelAsync(model => SensorAt(model, 3));
        Assert.Multiple(() =>
        {
            Assert.That(rtdSensor?.RRef, Is.EqualTo(rtdReference).Within(1e-3), "an RTD's R is sensors.analog[].rRef");
            Assert.That(rtdSensor?.Wires, Is.EqualTo(wires), "W is sensors.analog[].wires");
            Assert.That(rtdSensor?.MainsFrequency, Is.EqualTo(mainsFrequency), "F is sensors.analog[].mainsFrequency");
            Assert.That(thermocoupleSensor?.ThermocoupleType, Is.EqualTo(thermocoupleType.ToString()), "K is sensors.analog[].thermocoupleType");
            Assert.That(thermocoupleSensor?.MainsFrequency, Is.EqualTo(mainsFrequency));
        });
    }

    /// <summary>
    /// M308 S with no other parameter reports the sensor: the board is asked with S alone and its
    /// report reaches the caller with the sensor's name
    /// </summary>
    /// <remarks>
    /// RemoteSensor::Configure prefixes a reply starting "type " with "Sensor n (name) ". Regression
    /// case m308-sensor-name-and-report: the report sends paramMap 0x00080, S alone
    /// </remarks>
    [Test]
    public async Task M308WithOnlySAsksTheBoardForItsReport()
    {
        string boardReport = Invariant($"type thermistor using pin {ThermistorPort}, last error ok, reading 0.0, T:{R25:F1} B:{Beta:F1} C:0.00e+0 R:2200.0 L:0 H:0");
        await using Bench bench = await Bench.With($"{ThermistorSensor} A\"Test sensor\"")
                                             .WithController(canMaster => canMaster.ReportWith<CanMessageM308V1>(
                                                 sent => sent.Y is null ? boardReport : string.Empty))
                                             .StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M308 S{SensorNumber}");

        Assert.That(SentM308(bench), Has.Count.EqualTo(1), "the board is asked once");
        (byte board, CanMessageM308V1 query) = SentM308(bench)[0];
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Not.StartWith("Error:").And.Not.StartWith("Warning:"), "a report is not a failure");
            Assert.That(reply, Does.Contain(boardReport), "the board's report reaches the caller");
            Assert.That(reply, Does.Contain("Test sensor"), "with the sensor's name, which only this side knows");
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the query goes to the sensor's board");
            Assert.That(query.S, Is.EqualTo(SensorNumber), "naming the sensor");
            Assert.That(query.P, Is.Null, "and nothing else: no port");
            Assert.That(query.Y, Is.Null, "no type, which would recreate the sensor");
            Assert.That(query.T, Is.Null);
            Assert.That(query.B, Is.Null);
        });
    }

    /// <summary>
    /// M308 without Y amends the existing sensor: only the parameters given are sent, to the board the
    /// sensor is already on, and the sensor keeps its type and port. A renames it on this side
    /// </summary>
    /// <remarks>
    /// Heat::ConfigureSensor falls through to sensor->Configure for an existing sensor. Regression case
    /// m308-adc-offset-correction sends {S, H 10, L -10} (paramMap 0x000B0). A thermistor's L and H
    /// are ADC corrections (Thermistor::Configure), not the linear analog sensor's readings that
    /// sensors.analog[].lowReading and .highReading document
    /// </remarks>
    [Test]
    public async Task M308WithoutYAmendsTheExistingSensor()
    {
        const int high = 10, low = -10;
        await using Bench bench = await Bench.StartAsync($"{ThermistorSensor} A\"Test sensor\"");

        string reply = await bench.Host.ExecuteCodeAsync($"M308 S{SensorNumber} H{high} L{low} A\"renamed\"");

        Assert.That(SentM308(bench), Has.Count.EqualTo(1), "one M308 goes to the board");
        (byte board, CanMessageM308V1 sent) = SentM308(bench)[0];
        AnalogSensor? sensor = await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber));
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Not.StartWith("Error:"));
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the change goes to the board the sensor is on");
            Assert.That(sent.S, Is.EqualTo(SensorNumber));
            Assert.That(sent.H, Is.EqualTo(high), "H as written");
            Assert.That(sent.L, Is.EqualTo(low), "L as written");
            Assert.That(sent.P, Is.Null, "no port, which the code did not give");
            Assert.That(sent.Y, Is.Null, "no type, which would recreate the sensor");
            Assert.That(sent.T, Is.Null, "nor the coefficients the code did not touch");
            Assert.That(sensor?.Type, Is.EqualTo(AnalogSensorType.Thermistor), "the sensor is still a thermistor");
            Assert.That(sensor?.Port, Is.EqualTo(ThermistorPort), "on the same port");
            Assert.That(sensor?.R25, Is.EqualTo(R25).Within(1e-3), "with the same coefficients");
            Assert.That(sensor?.Name, Is.EqualTo("renamed"), "A renames the sensor");
            Assert.That(sensor?.LowReading, Is.Null, "a thermistor's L is an ADC correction, not a low reading");
            Assert.That(sensor?.HighReading, Is.Null, "a thermistor's H is an ADC correction, not a high reading");
            Assert.That(sensor?.AdcLowOffset, Is.EqualTo(low), "L is sensors.analog[2].adcLowOffset");
            Assert.That(sensor?.AdcHighOffset, Is.EqualTo(high), "H is sensors.analog[2].adcHighOffset");
        });
    }

    /// <summary>
    /// A thermistor's B without C resets C to 0, and a new port on the same board clears the ADC
    /// corrections, as the board does with its own copy
    /// </summary>
    /// <remarks>
    /// Thermistor::Configure: "if user changes B and doesn't define C, assume C=0", and a port change calls
    /// InitPort, which clears the ADC corrections. The board receives only what the code gave
    /// </remarks>
    [Test]
    public async Task M308ThermistorBResetsCAndANewPortClearsTheAdcCorrections()
    {
        const float newBeta = 3950.0f;
        const int low = -10, high = 10;
        string newPort = Expansion.Temp(Machines.DriverBoard, 1);
        await using Bench bench = await Bench.StartAsync(Invariant($"{ThermistorSensor} C{ShCText} L{low} H{high}"));

        await bench.Host.ExecuteCodeAsync(Invariant($"M308 S{SensorNumber} B{newBeta}"));
        AnalogSensor? afterB = await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber));
        (_, CanMessageM308V1 sentB) = bench.CanMaster.LastCanMessage<CanMessageM308V1>();
        Assert.Multiple(() =>
        {
            Assert.That(sentB.B, Is.EqualTo(newBeta).Within(1e-3), "B is sent as written");
            Assert.That(sentB.C, Is.Null, "and C is not sent, the board resets its own");
            Assert.That(afterB?.Beta, Is.EqualTo(newBeta).Within(1e-3));
            Assert.That(afterB?.C, Is.Zero, "B without C makes C 0");
            Assert.That(afterB?.AdcLowOffset, Is.EqualTo(low), "the ADC corrections are untouched by B");
        });

        string reply = await bench.Host.ExecuteCodeAsync($"M308 S{SensorNumber} P\"{newPort}\"");
        AnalogSensor? afterP = await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber));
        (byte board, CanMessageM308V1 sentP) = bench.CanMaster.LastCanMessage<CanMessageM308V1>();
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Not.StartWith("Error:"), "a port on the same board needs no Y");
            Assert.That(board, Is.EqualTo(Machines.DriverBoard));
            Assert.That(sentP.P, Is.EqualTo("temp1"), "the new port goes to the board");
            Assert.That(sentP.Y, Is.Null);
            Assert.That(afterP?.Port, Is.EqualTo(newPort), "sensors.analog[2].port follows");
            Assert.That(afterP?.Type, Is.EqualTo(AnalogSensorType.Thermistor));
            Assert.That(afterP?.Beta, Is.EqualTo(newBeta).Within(1e-3), "the coefficients stay");
            Assert.That(afterP?.AdcLowOffset, Is.Null, "a new port clears the ADC corrections");
            Assert.That(afterP?.AdcHighOffset, Is.Null);
        });
    }

    /// <summary>
    /// An amendment the board refuses is an error and leaves what the object model records about the
    /// sensor as it was, apart from the name, which is this side's alone
    /// </summary>
    /// <remarks>
    /// RemoteSensor::Configure applies A (ConfigureCommonParameters) before it sends the rest to the board,
    /// which holds every other setting and keeps its own when it refuses
    /// </remarks>
    [Test]
    public async Task M308KeepsTheSettingsOfARefusedAmendment()
    {
        const string refusal = "Bad thermistor parameters";
        await using Bench bench = await Bench.With($"{ThermistorSensor} U1")
                                             .WithController(canMaster => canMaster.OnCanMessage(
                (ushort)CanMessageType.M308V1, (fake, header, payload) =>
                {
                    bool refused = CanMessageSerializer.Deserialize<CanMessageM308V1>(payload).Y is null;
                    fake.InjectStandardReply(header, refused ? CodeResult.Error : CodeResult.Ok, refused ? refusal : string.Empty);
                })).StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync(Invariant($"M308 S{SensorNumber} T1000 B3000 U2 A\"renamed\""));

        AnalogSensor? sensor = await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber));
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:").And.Contain(refusal), "the board's refusal is the reply");
            Assert.That(sensor?.R25, Is.EqualTo(R25).Within(1e-3), "sensors.analog[2].r25 keeps what the board still has");
            Assert.That(sensor?.Beta, Is.EqualTo(Beta).Within(1e-3), "and so does beta");
            Assert.That(sensor?.OffsetAdj, Is.EqualTo(1.0f), "and the offset adjustment");
            Assert.That(sensor?.Name, Is.EqualTo("renamed"), "the name is set before the board is asked");
        });
    }

    /// <summary>
    /// U and V, the reading offset and slope adjustments, are sent to the board that applies them
    /// and recorded in sensors.analog[].offsetAdj and .slopeAdj
    /// </summary>
    /// <remarks>
    /// TemperatureSensor::ConfigureCommonParameters reads them, M308V1Params carries them as float16.
    /// RRF's RemoteSensor then clears its own copy (ClearAdjustments) so its object model reports 0
    /// for an expansion-board sensor; the object model here has to hold them to recreate the sensor
    /// </remarks>
    [Test]
    public async Task M308SendsAndRecordsTheReadingAdjustments()
    {
        const float offset = 1.5f, slope = -0.01f;
        await using Bench bench = await Bench.StartAsync(ThermistorSensor);

        await bench.Host.ExecuteCodeAsync(Invariant($"M308 S{SensorNumber} U{offset} V{slope}"));

        (_, CanMessageM308V1 sent) = bench.CanMaster.LastCanMessage<CanMessageM308V1>();
        AnalogSensor? sensor = await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber));
        Assert.Multiple(() =>
        {
            Assert.That(sent.U, Is.EqualTo(offset).Within(1e-2), "U reaches the board");
            Assert.That(sent.V, Is.EqualTo(slope).Within(1e-3), "V reaches the board");
            Assert.That(sensor?.OffsetAdj, Is.EqualTo(offset).Within(1e-3), "U is sensors.analog[2].offsetAdj");
            Assert.That(sensor?.SlopeAdj, Is.EqualTo(slope).Within(1e-4), "V is sensors.analog[2].slopeAdj");
        });
    }

    /// <summary>Lines refused before anything reaches a board, with sensor 2 already a thermistor</summary>
    private static readonly string[] RefusedLines =
    [
        "M308",                                                                 // MustSee('S')
        "M308 A\"Test sensor\"",                                                // A is not a selector
        "M308 S-1",                                                             // GetUIValue
        $"M308 S{CanLimits.MaxSensors} P\"{Expansion.Temp(Machines.DriverBoard, 1)}\" Y\"thermistor\"", // "Sensor number out of range"
        $"M308 S{SensorNumber} U20.5",                                          // TryGetLimitedFValue('U', -20, 20)
        $"M308 S{SensorNumber} U-21",
        $"M308 S{SensorNumber} V0.25",                                          // TryGetLimitedFValue('V', -0.2, 0.2)
        $"M308 S{SensorNumber} V-0.3",
        $"M308 S{SensorNumber} K\"KK\"",                                        // char_p: "expected single-character quoted string"
    ];

    /// <summary>
    /// A missing or out-of-range S, an adjustment outside its limits or a K that is not one letter is
    /// an error, sends nothing and leaves the sensors as they were
    /// </summary>
    /// <remarks>
    /// Heat::ConfigureSensor opens with gb.MustSee('S') and refuses S >= MaxSensors; the limits are
    /// ConfigureCommonParameters' and PopulateFromCommand's, which throw before the message is sent.
    /// Regression case error-missing-parameters-heaters records the bare M308 as an error. There is
    /// no list form
    /// </remarks>
    [TestCaseSource(nameof(RefusedLines))]
    public async Task M308RefusesBeforeTellingTheBoard(string line)
    {
        await using Bench bench = await Bench.StartAsync(ThermistorSensor);

        string reply = await bench.Host.ExecuteCodeAsync(line);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"), "the line is refused");
            Assert.That(SentM308(bench), Is.Empty, "and no board hears of it");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Analog.Count(sensor => sensor is not null)), Is.EqualTo(1),
                        "no sensor is created");
            Assert.That(await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber)?.OffsetAdj), Is.Zero,
                        "and the existing one keeps its offset adjustment");
            Assert.That(await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber)?.SlopeAdj), Is.Zero,
                        "and its slope adjustment");
        });
    }

    /// <summary>What a line without Y may carry: nothing, a coefficient or a port</summary>
    private static readonly string[] ParametersWithoutY = ["", Invariant($" T{R25}"), $" P\"{ThermistorPort}\""];

    /// <summary>
    /// A sensor that does not exist cannot be reported or amended, nor given a port without a type
    /// </summary>
    /// <remarks>
    /// Heat::ConfigureSensor without Y: "Sensor %u does not exist". Regression cases m308-delete-sensor
    /// and m308-error-bad-port record M308 S2 after the sensor is gone as that error, with no CAN
    /// </remarks>
    [TestCaseSource(nameof(ParametersWithoutY))]
    public async Task M308RefusesASensorThatDoesNotExist(string parameters)
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M308 S{SensorNumber}{parameters}");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"), "there is no sensor to report or amend");
            Assert.That(SentM308(bench), Is.Empty, "no board is asked");
            Assert.That(await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber)), Is.Null,
                        "and sensors.analog[2] is not created");
        });
    }

    /// <summary>"nil" alone and behind the sensor's board address</summary>
    private static readonly string[] NilPorts = ["nil", $"{Machines.DriverBoard}.nil"];

    /// <summary>
    /// M308 P"nil" deletes the sensor: the board it was on is told to release it, sensors.analog[]
    /// loses it, and reporting or deleting it again finds nothing
    /// </summary>
    /// <remarks>
    /// Heat::ConfigureSensor removes the board address and compares with NoPinName before anything
    /// else, so P"nil" and P"1.nil" are the same; ~RemoteSensor sends {S, P "nil"} to the sensor's
    /// board. Regression case m308-delete-sensor: paramMap 0x00880, then "Sensor 2 does not exist",
    /// and sensors.analog shrinks to [] as RRF reports up to the highest sensor
    /// (GetNumSensorsToReport). Deleting a missing sensor is harmless (its teardown)
    /// </remarks>
    [TestCaseSource(nameof(NilPorts))]
    public async Task M308PNilDeletesTheSensor(string nil)
    {
        await using Bench bench = await Bench.StartAsync(ThermistorSensor);

        string deleted = await bench.Host.ExecuteCodeAsync($"M308 S{SensorNumber} P\"{nil}\"");

        Assert.That(SentM308(bench), Has.Count.EqualTo(1), "one release goes out");
        (byte board, CanMessageM308V1 release) = SentM308(bench)[0];
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(deleted, Does.Not.StartWith("Error:"), "P\"nil\" is a delete, not a bad port");
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the release goes to the sensor's board");
            Assert.That(release.S, Is.EqualTo(SensorNumber));
            Assert.That(release.P, Is.EqualTo("nil"), "P\"nil\" frees the port on the board");
            Assert.That(release.Y, Is.Null, "and nothing else");
            Assert.That(await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber)), Is.Null,
                        "sensors.analog[2] is gone");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Analog.Count), Is.Zero,
                        "and sensors.analog[] ends at the highest sensor left, here none");
        });

        bench.CanMaster.ClearCapture();
        string again = await bench.Host.ExecuteCodeAsync($"M308 S{SensorNumber} P\"nil\"");
        string report = await bench.Host.ExecuteCodeAsync($"M308 S{SensorNumber}");
        Assert.Multiple(() =>
        {
            Assert.That(again, Does.Not.StartWith("Error:"), "deleting a sensor that is gone is harmless");
            Assert.That(report, Does.StartWith("Error:"), "reporting it is an error");
            Assert.That(SentM308(bench), Is.Empty, "and neither reaches a board");
        });
    }

    /// <summary>
    /// M308 Y on an existing sensor replaces it: the old board is told to release it, the new one is
    /// configured, and the new sensor starts fresh rather than inheriting the old one's settings
    /// </summary>
    /// <remarks>
    /// Heat::ConfigureSensor calls DeleteSensor before TemperatureSensor::Create whenever Y is seen,
    /// and ~RemoteSensor sends {S, P "nil"} to the board the old sensor was on
    /// </remarks>
    [Test]
    public async Task M308WithYReplacesTheSensor()
    {
        string newPort = Expansion.Temp(OtherBoard, 1);
        await using Bench bench = await Bench.StartAsync($"{ThermistorSensor} A\"Test sensor\" U1");

        string reply = await bench.Host.ExecuteCodeAsync($"M308 S{SensorNumber} P\"{newPort}\" Y\"pt1000\"");

        IReadOnlyList<(byte Board, CanMessageM308V1 Message)> sent = SentM308(bench);
        Assert.That(sent, Has.Count.EqualTo(2), "a release and a configuration");
        AnalogSensor? sensor = await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber));
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Not.StartWith("Error:"));
            Assert.That(sent[0].Board, Is.EqualTo(Machines.DriverBoard), "first the old board");
            Assert.That(sent[0].Message.P, Is.EqualTo("nil"), "is told to release the sensor");
            Assert.That(sent[1].Board, Is.EqualTo(OtherBoard), "then the new board");
            Assert.That(sent[1].Message.Y, Is.EqualTo("pt1000"), "is given the new sensor");
            Assert.That(sent[1].Message.P, Is.EqualTo("temp1"), "on its port");
            Assert.That(sensor?.Type, Is.EqualTo(AnalogSensorType.PT1000), "sensors.analog[2] is the new sensor");
            Assert.That(sensor?.Port, Is.EqualTo(newPort), "on the new port");
            Assert.That(sensor?.Name, Is.Null.Or.Empty, "with no name, since A was not given again");
            Assert.That(sensor?.R25, Is.Null, "and none of the old thermistor's coefficients");
            Assert.That(sensor?.Beta, Is.Null);
            Assert.That(sensor?.OffsetAdj, Is.Zero, "nor its offset adjustment");
        });
    }

    /// <summary>
    /// P naming another board without Y is refused: a sensor is moved only by recreating it
    /// </summary>
    /// <remarks>
    /// Heat::ConfigureSensor: "Sensor type parameter needed when moving a sensor to a different board"
    /// </remarks>
    [Test]
    public async Task M308RefusesToMoveASensorToAnotherBoardWithoutY()
    {
        await using Bench bench = await Bench.StartAsync(ThermistorSensor);

        string reply = await bench.Host.ExecuteCodeAsync($"M308 S{SensorNumber} P\"{Expansion.Temp(OtherBoard, 0)}\"");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"), "a sensor cannot change board without a type");
            Assert.That(SentM308(bench), Is.Empty, "no board hears of it");
            Assert.That(await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber)?.Port), Is.EqualTo(ThermistorPort),
                        "and the sensor stays where it was");
        });
    }

    /// <summary>
    /// A new sensor the board refuses is reported as an error and left out of the object model, and the
    /// board is told to release whatever it may have claimed
    /// </summary>
    /// <remarks>
    /// Heat::ConfigureSensor deletes a new sensor whose Configure did not succeed, and ~RemoteSensor
    /// sends {S, P "nil"}. Regression cases m308-error-bad-port ("Unknown pin name 'temp9'"),
    /// m308-rtd-max31865 and m308-thermocouple-max31856 ("Failed to initialise ...": resultCode 4,
    /// then P"nil", then "Sensor 2 does not exist")
    /// </remarks>
    [Test]
    public async Task M308DropsASensorTheBoardRefuses()
    {
        const string refusal = "Unknown pin name 'temp9'";
        await using Bench bench = await Bench.With()
                                             .WithController(canMaster => canMaster.OnCanMessage(
                (ushort)CanMessageType.M308V1, (fake, header, payload) =>
                {
                    bool refused = CanMessageSerializer.Deserialize<CanMessageM308V1>(payload).Y is not null;
                    fake.InjectStandardReply(header, refused ? CodeResult.Error : CodeResult.Ok, refused ? refusal : string.Empty);
                })).StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync(Invariant(
            $"M308 S{SensorNumber} P\"{Expansion.Temp(Machines.DriverBoard, 9)}\" Y\"thermistor\" T{R25} B{Beta}"));

        IReadOnlyList<(byte Board, CanMessageM308V1 Message)> sent = SentM308(bench);
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"), "the board's refusal is an error");
            Assert.That(reply, Does.Contain(refusal), "carrying the board's reason");
            Assert.That(await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber)), Is.Null,
                        "and sensors.analog[2] does not hold the sensor that was refused");
            Assert.That(sent.Select(s => s.Message.P), Is.EqualTo(new[] { "temp9", "nil" }),
                        "the refused configuration is followed by a release to the same board");
            Assert.That(sent.Select(s => s.Board), Is.All.EqualTo(Machines.DriverBoard));
        });
    }

    /// <summary>
    /// A replacement the board refuses leaves no sensor at all: the old one was released before the
    /// new one was tried
    /// </summary>
    /// <remarks>Heat::ConfigureSensor calls DeleteSensor before TemperatureSensor::Create</remarks>
    [Test]
    public async Task M308ReplacementTheBoardRefusesLeavesNoSensor()
    {
        await using Bench bench = await Bench.With(ThermistorSensor)
                                             .WithController(canMaster => canMaster.OnCanMessage(
                (ushort)CanMessageType.M308V1, (fake, header, payload) =>
                {
                    bool refused = CanMessageSerializer.Deserialize<CanMessageM308V1>(payload).Y == "pt1000";
                    fake.InjectStandardReply(header, refused ? CodeResult.Error : CodeResult.Ok,
                                             refused ? "Unknown sensor type name" : string.Empty);
                })).StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M308 S{SensorNumber} P\"{ThermistorPort}\" Y\"pt1000\"");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber)), Is.Null,
                        "neither the refused sensor nor the thermistor it replaced is left in sensors.analog[2]");
            Assert.That(SentM308(bench).First().Message.P, Is.EqualTo("nil"),
                        "the thermistor was released before the replacement was tried");
        });
    }

    /// <summary>A board's warning is passed on and the sensor is kept</summary>
    /// <remarks>Heat::ConfigureSensor inserts the sensor when Succeeded(rslt), which includes warning</remarks>
    [Test]
    public async Task M308KeepsASensorTheBoardWarnsAbout()
    {
        const string warning = "sensor reading is out of range";
        await using Bench bench = await Bench.With()
                                             .WithController(canMaster => canMaster.ReportWith<CanMessageM308V1>(warning, CodeResult.Warning))
                                             .StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync(ThermistorSensor);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply, Does.StartWith("Warning:"), "the board's warning is a warning here");
            Assert.That(reply, Does.Contain(warning), "carrying the board's text");
            Assert.That(await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber)?.Type), Is.EqualTo(AnalogSensorType.Thermistor),
                        "and the sensor is kept");
        });
    }

    /// <summary>
    /// A board that never answers makes M308 an error, and the sensor it was meant to create is not
    /// kept
    /// </summary>
    /// <remarks>
    /// CanMessageGenericConstructor::SendAndGetResponse returns an error on a timeout, which
    /// Heat::ConfigureSensor treats like any other failure: the new sensor is deleted
    /// </remarks>
    [Test]
    public async Task M308DropsASensorWhoseBoardNeverAnswers()
    {
        await using Bench bench = await Bench.With()
                                             .WithController(canMaster => canMaster.NeverAnswer<CanMessageM308V1>())
                                             .StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync(ThermistorSensor);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"), "a timeout is an error");
            Assert.That(await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber)), Is.Null,
                        "and sensors.analog[2] does not hold a sensor no board confirmed");
        });
    }

    /// <summary>
    /// A sensor on board 0 is refused: it runs DuetCANMaster and owns no pins, so a sensor port has to
    /// name an expansion board, whether board 0 is named or implied
    /// </summary>
    /// <remarks>
    /// A documented deviation (rrf-differences.md, "A port name has to name an expansion board");
    /// RepRapFirmware creates the sensor on its main board
    /// </remarks>
    [TestCase("temp0")]
    [TestCase("0.temp0")]
    public async Task M308RefusesASensorPortOnBoardZero(string port)
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M308 S{SensorNumber} P\"{port}\" Y\"thermistor\"");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"), "board 0 has no sensor inputs");
            Assert.That(SentM308(bench), Is.Empty, "no board is asked");
            Assert.That(await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber)), Is.Null,
                        "and no sensor is created");
        });
    }

    /// <summary>
    /// M308 Y"mcu-temp" with no P is the main board's own MCU temperature sensor: it needs no port and
    /// no expansion board, and reads what the controller reports as boards[0].mcuTemp
    /// </summary>
    /// <remarks>
    /// Heat::ConfigureSensor defaults the board to the main board's own address when P is absent.
    /// Regression case m308-mcu-temp-sensor: <c>M308 S3 Y"mcu-temp" A"main MCU"</c> sends no CAN
    /// and reports type "mcutemp" with a live reading. Here the main board is DuetCANMaster, which
    /// reports its MCU temperature over SPI
    /// </remarks>
    [Test]
    public async Task M308CreatesTheMainBoardMcuTemperatureSensor()
    {
        const float mcuTemp = 40.5f;
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M308 S3 Y\"mcu-temp\" A\"main MCU\"");

        AnalogSensor? sensor = await bench.Host.ReadModelAsync(model => SensorAt(model, 3));
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Not.StartWith("Error:"), "a pinless sensor needs no port");
            Assert.That(sensor?.Type, Is.EqualTo(AnalogSensorType.McuTemp), "sensors.analog[3] is the MCU sensor");
            Assert.That(sensor?.Name, Is.EqualTo("main MCU"));
            Assert.That(SentM308(bench), Is.Empty, "and no expansion board is involved");
        });

        bench.CanMaster.InjectControllerBoardStatus(mcuTemp: mcuTemp);
        await bench.Host.WaitForModelAsync(model => SensorAt(model, 3)?.LastReading,
                                           reading => reading is > mcuTemp - 0.1f and < mcuTemp + 0.1f,
                                           "sensors.analog[3].lastReading following the controller's MCU temperature",
                                           timeoutMs: 5_000);
    }

    /// <summary>
    /// M308 S on the main board's MCU temperature sensor is answered here, since no expansion board holds
    /// it, and U and V adjust the reading it takes from boards[0].mcuTemp
    /// </summary>
    /// <remarks>
    /// TemperatureSensor::Configure reports through CopyBasicDetails when nothing changes; regression case
    /// m308-mcu-temp-sensor: "Sensor 3 (main MCU) type Microcontroller embedded temperature sensor, last
    /// error ok, reading 40.3". TemperatureSensor::SetResult applies t * (1 + V) + U
    /// </remarks>
    [Test]
    public async Task M308ReportsAndAdjustsTheMainBoardMcuTemperatureSensor()
    {
        const float mcuTemp = 40.5f, offset = 1.5f;
        await using Bench bench = await Bench.StartAsync("M308 S3 Y\"mcu-temp\" A\"main MCU\"");
        bench.CanMaster.InjectControllerBoardStatus(mcuTemp: mcuTemp);
        await bench.Host.WaitForModelAsync(model => SensorAt(model, 3)?.LastReading,
                                           reading => reading is > mcuTemp - 0.1f and < mcuTemp + 0.1f,
                                           "sensors.analog[3].lastReading following the controller", timeoutMs: 5_000);

        string report = await bench.Host.ExecuteCodeAsync("M308 S3");
        Assert.Multiple(() =>
        {
            Assert.That(report.Trim(), Is.EqualTo(Invariant($"Sensor 3 (main MCU) type Microcontroller embedded temperature sensor, last error ok, reading {mcuTemp:F1}")),
                        "the report is CopyBasicDetails'");
            Assert.That(SentM308(bench), Is.Empty, "and no board is asked");
        });

        string adjusted = await bench.Host.ExecuteCodeAsync(Invariant($"M308 S3 U{offset}"));
        bench.CanMaster.InjectControllerBoardStatus(mcuTemp: mcuTemp);
        await bench.Host.WaitForModelAsync(model => SensorAt(model, 3)?.LastReading,
                                           reading => reading is > mcuTemp + offset - 0.1f and < mcuTemp + offset + 0.1f,
                                           "sensors.analog[3].lastReading carrying the offset", timeoutMs: 5_000);
        Assert.That(adjusted.Trim(), Is.Empty, "a change is not a report");
        Assert.That(await bench.Host.ReadModelAsync(model => SensorAt(model, 3)?.OffsetAdj), Is.EqualTo(offset), "U is sensors.analog[3].offsetAdj");
        Assert.That(SentM308(bench), Is.Empty);
    }

    /// <summary>Types that need a port, and the drivers sensor, which board 0 has no drivers for</summary>
    private static readonly string[] PortlessTypes = ["thermistor", "drivers", "no-such-type"];

    /// <summary>
    /// Y without P is a sensor on the main board, which reads only its MCU temperature: anything else is
    /// refused and leaves no sensor
    /// </summary>
    /// <remarks>
    /// RepRapFirmware creates a local sensor, whose Configure refuses a missing port ("Missing port name
    /// parameter") and whose Create refuses an unknown type ("Unknown sensor type name"); its main board has
    /// drivers, which board 0 here does not (rrf-differences.md, "A port name has to name an expansion board")
    /// </remarks>
    [TestCaseSource(nameof(PortlessTypes))]
    public async Task M308RefusesAMainBoardSensorOtherThanTheMcu(string typeName)
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M308 S{SensorNumber} Y\"{typeName}\"");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(SentM308(bench), Is.Empty, "no board is asked");
            Assert.That(await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber)), Is.Null, "and no sensor is created");
        });
    }

    /// <summary>A board's report carries the sensor's error state as well as its reading</summary>
    /// <remarks>TemperatureSensor::SetResult stores lastResult; regression case m950-heater-on-expansion records openCircuit</remarks>
    [Test]
    public async Task ASensorReportCarriesTheErrorState()
    {
        await using Bench bench = await Bench.StartAsync(ThermistorSensor);

        bench.CanMaster.InjectSensorReport(Machines.DriverBoard, SensorNumber, temperature: 2000.0f, TemperatureError.OpenCircuit);

        await bench.Host.WaitForModelAsync(model => SensorAt(model, SensorNumber)?.State,
                                           state => state == TemperatureError.OpenCircuit,
                                           "sensors.analog[2].state becoming openCircuit", timeoutMs: 5_000);
    }

    /// <summary>
    /// A report naming a sensor that lives on another board is ignored, and a report for a sensor the
    /// machine does not have creates nothing
    /// </summary>
    /// <remarks>
    /// RemoteSensor::UpdateRemoteTemperature applies a report only when src == boardAddress, and
    /// Heat::ProcessRemoteSensorsReport looks each sensor up rather than creating it
    /// </remarks>
    [Test]
    public async Task ASensorReportIsTakenOnlyFromTheSensorsBoard()
    {
        const float reading = 23.5f, wrongReading = 99.0f, markerReading = 50.0f;
        const int markerSensor = 3, unknownSensor = 5;
        await using Bench bench = await Bench.StartAsync(
            ThermistorSensor + $"\nM308 S{markerSensor} P\"{Expansion.Temp(Machines.DriverBoard, 1)}\" Y\"thermistor\"");

        bench.CanMaster.InjectSensorReport(Machines.DriverBoard, SensorNumber, reading);
        await bench.Host.WaitForModelAsync(model => SensorAt(model, SensorNumber)?.LastReading,
                                           value => value is > reading - 0.1f and < reading + 0.1f,
                                           "the sensor's own board's report arriving", timeoutMs: 5_000);

        // Reports are applied in order, so once the marker sensor's report is in, the two before it have been seen
        bench.CanMaster.InjectSensorReport(OtherBoard, SensorNumber, wrongReading);
        bench.CanMaster.InjectSensorReport(OtherBoard, unknownSensor, wrongReading);
        bench.CanMaster.InjectSensorReport(Machines.DriverBoard, markerSensor, markerReading);
        await bench.Host.WaitForModelAsync(model => SensorAt(model, markerSensor)?.LastReading,
                                           value => value is > markerReading - 0.1f and < markerReading + 0.1f,
                                           "the marker sensor's report arriving", timeoutMs: 5_000);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber)?.LastReading), Is.EqualTo(reading).Within(0.01),
                        "a report from another board does not overwrite the reading");
            Assert.That(await bench.Host.ReadModelAsync(model => SensorAt(model, unknownSensor)), Is.Null,
                        "and a report for a sensor that was never created adds nothing");
        });
    }

    /// <summary>
    /// A sensor whose board stops reporting times out: its state becomes timeout and its reading the
    /// error temperature
    /// </summary>
    /// <remarks>
    /// RemoteSensor::Poll calls CheckTimeout, which after DefaultTemperatureReadingTimeout (2 s) without
    /// a report sets lastTemperature to BadErrorTemperature (2000) and lastResult to timeout
    /// </remarks>
    [Test]
    [Category("LongRunning")]
    public async Task ASensorWhoseBoardStopsReportingTimesOut()
    {
        const float reading = 23.5f;
        await using Bench bench = await Bench.StartAsync(ThermistorSensor);

        bench.CanMaster.InjectSensorReport(Machines.DriverBoard, SensorNumber, reading);
        await bench.Host.WaitForModelAsync(model => SensorAt(model, SensorNumber)?.LastReading,
                                           value => value is > reading - 0.1f and < reading + 0.1f,
                                           "the first report arriving", timeoutMs: 5_000);

        await bench.Host.WaitForModelAsync(model => SensorAt(model, SensorNumber)?.State,
                                           state => state == TemperatureError.Timeout,
                                           "sensors.analog[2].state timing out without reports", timeoutMs: 6_000);
        Assert.That(await bench.Host.ReadModelAsync(model => SensorAt(model, SensorNumber)?.LastReading), Is.EqualTo(2000.0f).Within(0.01),
                    "a timed-out sensor reads BadErrorTemperature");
    }
}
