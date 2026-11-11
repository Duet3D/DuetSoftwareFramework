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

/// <summary>M308, the temperature sensors</summary>
[TestFixture]
[GCode("M308")]
public class M308Tests : BenchFixture
{
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
}
