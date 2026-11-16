using System;
using DuetAPI.ObjectModel;
using DuetControlServer;
using DuetControlServer.Heat;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace UnitTests.Heaters;

/// <summary>
/// Tests for <see cref="SensorManager"/>: the sensor list, where its readings are taken from, and their timeout
/// </summary>
[TestFixture]
public class SensorManagerTests
{
    private DuetControlServer.Model.ObjectModel _model = null!;
    private ManualClock _clock = null!;
    private SensorManager _sensors = null!;

    [SetUp]
    public void CreateManager()
    {
        _model = new(new StoppedLifetime(), NullLogger<DuetControlServer.Model.ObjectModel>.Instance, Options.Create(new Settings()));
        _clock = new ManualClock();
        _sensors = new SensorManager(_model, _clock);
    }

    [TearDown]
    public void DisposeManager() => _sensors.Dispose();

    /// <summary>A sensor of the given number on the given port</summary>
    private AnalogSensor Create(int sensorNumber, string? port, AnalogSensorType type = AnalogSensorType.Thermistor)
    {
        AnalogSensor sensor = _sensors.Create(sensorNumber);
        sensor.Port = port;
        sensor.Type = type;
        return sensor;
    }

    [Test]
    public void ANewSensorIsNotReadyAndHoldsItsNumber()
    {
        AnalogSensor sensor = Create(3, "1.temp0");

        Assert.Multiple(() =>
        {
            Assert.That(_model.Sensors.Analog, Has.Count.EqualTo(4), "sensors.analog[] is indexed by sensor number");
            Assert.That(_model.Sensors.Analog[3], Is.SameAs(sensor));
            Assert.That(_model.Sensors.Analog[0], Is.Null);
            Assert.That(sensor.State, Is.EqualTo(TemperatureError.NotReady));
        });
    }

    [Test]
    public void DeletingTrimsTheListToTheHighestSensorLeft()
    {
        Create(1, "1.temp0");
        Create(4, "1.temp1");
        Create(2, "1.temp2");

        _sensors.Delete(4);
        Assert.That(_model.Sensors.Analog, Has.Count.EqualTo(3), "the slots above sensor 2 go");

        _sensors.Delete(1);
        Assert.That(_model.Sensors.Analog, Has.Count.EqualTo(3), "a gap below the highest sensor stays");
        Assert.That(_model.Sensors.Analog[1], Is.Null);

        _sensors.Delete(2);
        Assert.That(_model.Sensors.Analog, Is.Empty);

        Assert.That(() => _sensors.Delete(7), Throws.Nothing, "deleting a sensor that does not exist is harmless");
    }

    [Test]
    public void AReadingIsTakenOnlyFromTheSensorsOwnBoard()
    {
        AnalogSensor sensor = Create(2, "1.temp0");

        _sensors.ApplyRemoteReading(source: 1, 2, 23.5f, TemperatureError.Ok);
        _sensors.ApplyRemoteReading(source: 2, 2, 99.0f, TemperatureError.OpenCircuit);
        _sensors.ApplyRemoteReading(source: 1, 5, 99.0f, TemperatureError.Ok);

        Assert.Multiple(() =>
        {
            Assert.That(sensor.LastReading, Is.EqualTo(23.5f));
            Assert.That(sensor.State, Is.EqualTo(TemperatureError.Ok));
            Assert.That(_model.Sensors.Analog, Has.Count.EqualTo(3), "a report for a sensor that does not exist adds nothing");
        });
    }

    [Test]
    public void TheMainBoardMcuSensorIsAdjustedAndTheLastRealErrorKept()
    {
        AnalogSensor mcu = Create(0, port: null, AnalogSensorType.McuTemp);
        mcu.OffsetAdj = 1.0f;
        mcu.SlopeAdj = 0.1f;
        AnalogSensor remote = Create(1, "1.temp0", AnalogSensorType.McuTemp);

        _sensors.ApplyMcuTemperature(40.0f);
        _sensors.ApplyRemoteReading(source: 1, 1, 2000.0f, TemperatureError.ShortCircuit);
        _sensors.ApplyRemoteReading(source: 1, 1, 30.0f, TemperatureError.Ok);

        Assert.Multiple(() =>
        {
            Assert.That(mcu.LastReading, Is.EqualTo((40.0f * 1.1f) + 1.0f).Within(1e-4), "t * (1 + V) + U, as TemperatureSensor::SetResult");
            Assert.That(mcu.State, Is.EqualTo(TemperatureError.Ok));
            Assert.That(remote.LastReading, Is.EqualTo(30.0f), "an expansion board's MCU sensor is that board's, not boards[0]'s");
            Assert.That(_sensors.GetLastRealError(1), Is.EqualTo(TemperatureError.ShortCircuit), "ok does not clear the last error");
            Assert.That(_sensors.GetLastRealError(0), Is.EqualTo(TemperatureError.Ok));
        });
    }

    [Test]
    public void ASensorWithoutReadingsTimesOut()
    {
        AnalogSensor sensor = Create(2, "1.temp0");
        _clock.Advance(SensorManager.ReadingTimeout);
        _sensors.CheckTimeouts();
        Assert.That(sensor.State, Is.EqualTo(TemperatureError.NotReady), "not until the timeout has passed");

        _sensors.ApplyRemoteReading(source: 1, 2, 23.5f, TemperatureError.Ok);
        _clock.Advance(SensorManager.ReadingTimeout);
        _sensors.CheckTimeouts();
        Assert.That(sensor.LastReading, Is.EqualTo(23.5f), "a reading restarts the clock");

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        _sensors.CheckTimeouts();
        Assert.Multiple(() =>
        {
            Assert.That(sensor.State, Is.EqualTo(TemperatureError.Timeout));
            Assert.That(sensor.LastReading, Is.EqualTo(HeatManager.BadErrorTemperature));
        });
    }

    [Test]
    public void AReplacedSensorIsTimedFromItsCreation()
    {
        Create(2, "1.temp0");
        _clock.Advance(SensorManager.ReadingTimeout);
        _sensors.Delete(2);
        AnalogSensor replacement = Create(2, "1.temp0");

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        _sensors.CheckTimeouts();

        Assert.That(replacement.State, Is.EqualTo(TemperatureError.NotReady));
    }

    [TestCase("1.temp0", 1)]
    [TestCase("!12.io1.in", 12)]
    [TestCase(null, 0)]
    public void ASensorIsOnTheBoardItsPortNames(string? port, int board)
        => Assert.That(SensorManager.GetBoard(new AnalogSensor { Port = port }), Is.EqualTo(board));

    /// <summary>A clock that moves only when told to</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;
    }

    /// <summary>An application lifetime that never fires, which is all the object model needs to be constructed</summary>
    private sealed class StoppedLifetime : IHostApplicationLifetime
    {
        public System.Threading.CancellationToken ApplicationStarted => System.Threading.CancellationToken.None;
        public System.Threading.CancellationToken ApplicationStopping => System.Threading.CancellationToken.None;
        public System.Threading.CancellationToken ApplicationStopped => System.Threading.CancellationToken.None;
        public void StopApplication() { }
    }
}
