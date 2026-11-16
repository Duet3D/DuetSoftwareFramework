using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using NUnit.Framework;

namespace UnitTests.Utility;

/// <summary>
/// Tests for <see cref="ReducedString"/>, RepRapFirmware's GetReducedString, and the sensor type names it is
/// used to match
/// </summary>
[TestFixture]
public class ReducedStringTests
{
    [TestCase("thermistor", "thermistor")]
    [TestCase("Linear_Analog", "linearanalog")]
    [TestCase("rtd-max31865", "rtdmax31865")]
    [TestCase("MCU temp", "mcutemp")]
    [TestCase("1.SPI.CS0", "1.spi.cs0")]
    [TestCase("!^io1.in", "!^io1.in")]
    [TestCase("", "")]
    public void DropsCaseHyphensUnderscoresAndSpaces(string written, string reduced)
        => Assert.That(ReducedString.Reduce(written), Is.EqualTo(reduced));

    [TestCase("thermistor", AnalogSensorType.Thermistor)]
    [TestCase("PT1000", AnalogSensorType.PT1000)]
    [TestCase("linear-analog", AnalogSensorType.LinearAnalog)]
    [TestCase("Linear_Analog", AnalogSensorType.LinearAnalog)]
    [TestCase("rtd-max31865", AnalogSensorType.MAX31865)]
    [TestCase("thermocouple-max31855", AnalogSensorType.MAX31855)]
    [TestCase("thermocouple-max31856", AnalogSensorType.MAX31856)]
    [TestCase("mcu-temp", AnalogSensorType.McuTemp)]
    [TestCase("mcutemp", AnalogSensorType.McuTemp)]
    [TestCase("drivers", AnalogSensorType.Drivers)]
    [TestCase("no such sensor", AnalogSensorType.Unknown)]
    public void ASensorTypeIsMatchedInItsReducedForm(string name, AnalogSensorType type)
        => Assert.That(AnalogSensorTypeConverter.Parse(name), Is.EqualTo(type));
}
