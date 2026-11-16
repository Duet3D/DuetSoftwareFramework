using System;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Model;
using Microsoft.Extensions.Hosting;

namespace DuetControlServer.Heat;

/// <summary>
/// The temperature sensors a machine has, and how recent their readings are
/// </summary>
/// <remarks>
/// <para>
/// Ported from the parts of RepRapFirmware's <c>Heat</c> and <c>TemperatureSensor</c> that keep the sensor
/// list and its readings. An expansion board reads each sensor on it and broadcasts the result, which
/// <c>ExpansionBoardManager</c> hands to <see cref="ApplyRemoteReading"/>; the main board's MCU temperature
/// sensor is fed from <c>boards[0].mcuTemp</c> instead. A sensor whose readings stop is timed out here, as
/// RepRapFirmware's heat task does when it polls it.
/// </para>
/// <para>
/// When each sensor was last read is transient, so it is kept here rather than in the object model. Every
/// member must be called with the object model write lock held, which is also what guards that state
/// </para>
/// </remarks>
public sealed class SensorManager : BackgroundService
{
    /// <summary>
    /// Number of sensors a machine may have
    /// </summary>
    /// <remarks>CANlib's <c>MaxSensors</c>, through the CAN message schema</remarks>
    public const int MaxSensors = CanLimits.MaxSensors;

    /// <summary>
    /// How long a sensor may go without a reading before it is timed out
    /// </summary>
    /// <remarks>RepRapFirmware's <c>TemperatureSensor::DefaultTemperatureReadingTimeout</c></remarks>
    public static readonly TimeSpan ReadingTimeout = TimeSpan.FromMilliseconds(2000);

    /// <summary>
    /// How often the sensors are checked for a timeout
    /// </summary>
    /// <remarks>RepRapFirmware's <c>HeatSampleIntervalMillis</c>, the period its heat task polls the sensors at</remarks>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// What the main board's MCU temperature sensor reports as its type
    /// </summary>
    /// <remarks>RepRapFirmware's <c>CpuTemperatureSensor</c> type description</remarks>
    public const string McuSensorDescription = "Microcontroller embedded temperature sensor";

    private readonly Model.ObjectModel _model;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Timestamp of each sensor's last reading, or of its creation until it has one
    /// </summary>
    private readonly long?[] _whenLastRead = new long?[MaxSensors];

    /// <summary>
    /// The last state other than ok that each sensor reported
    /// </summary>
    /// <remarks>RepRapFirmware's <c>TemperatureSensor::lastRealError</c>, which the M308 report quotes</remarks>
    private readonly TemperatureError[] _lastRealError = new TemperatureError[MaxSensors];

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="model">Object model</param>
    public SensorManager(Model.ObjectModel model) : this(model, TimeProvider.System) { }

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="model">Object model</param>
    /// <param name="timeProvider">Clock the reading timeouts are measured on</param>
    internal SensorManager(Model.ObjectModel model, TimeProvider timeProvider)
    {
        _model = model;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Find a sensor by number
    /// </summary>
    /// <param name="sensorNumber">The number</param>
    /// <returns>The sensor, or null if there is none</returns>
    public AnalogSensor? Find(int sensorNumber) => _model.Sensors.Analog.GetAt(sensorNumber);

    /// <summary>
    /// Put a new sensor in the object model, replacing any that held the number
    /// </summary>
    /// <param name="sensorNumber">The number</param>
    /// <returns>The sensor, which is not ready until its first reading</returns>
    /// <remarks>
    /// The collection is indexed by sensor number, so a machine that defines sensor 3 and nothing below
    /// it still has four entries. A new sensor starts <see cref="TemperatureError.NotReady"/>, as
    /// RepRapFirmware's <c>TemperatureSensor</c> constructor does, and times out if no reading follows
    /// </remarks>
    public AnalogSensor Create(int sensorNumber)
    {
        AnalogSensor sensor = _model.Sensors.Analog.SetAt(sensorNumber, new AnalogSensor { State = TemperatureError.NotReady });
        _whenLastRead[sensorNumber] = _timeProvider.GetTimestamp();
        _lastRealError[sensorNumber] = TemperatureError.Ok;
        return sensor;
    }

    /// <summary>
    /// Take a sensor out of the object model
    /// </summary>
    /// <param name="sensorNumber">The number</param>
    /// <remarks>
    /// <c>sensors.analog[]</c> ends at the highest sensor left, as RepRapFirmware reports it
    /// (<c>Heat::GetNumSensorsToReport</c>). Releasing the port on the board is the caller's
    /// </remarks>
    public void Delete(int sensorNumber)
    {
        if (Find(sensorNumber) is null)
        {
            return;
        }

        _model.Sensors.Analog.ClearAt(sensorNumber, trim: true);
        _whenLastRead[sensorNumber] = null;
    }

    /// <summary>
    /// The board that reads a sensor
    /// </summary>
    /// <param name="sensor">The sensor</param>
    /// <returns>Its CAN address, which is <see cref="CanId.MasterAddress"/> for the main board's own sensor</returns>
    /// <remarks>
    /// Read from <c>sensors.analog[].port</c>. Only the main board's MCU temperature sensor has no port,
    /// because every other sensor is on an expansion board
    /// </remarks>
    public static byte GetBoard(AnalogSensor sensor)
        => sensor.Port is string port ? IoPorts.RemoveBoardAddress(port, out _) : CanId.MasterAddress;

    /// <summary>
    /// The last state other than ok that a sensor reported, as the M308 report quotes it
    /// </summary>
    /// <param name="sensorNumber">The sensor</param>
    /// <returns>Its last error</returns>
    public TemperatureError GetLastRealError(int sensorNumber) => _lastRealError[sensorNumber];

    /// <summary>
    /// Apply a reading an expansion board reported
    /// </summary>
    /// <param name="source">CAN address of the board that sent it</param>
    /// <param name="sensorNumber">The sensor it is for</param>
    /// <param name="temperature">The reading, which the board has already adjusted</param>
    /// <param name="state">The sensor state the board reported</param>
    /// <remarks>
    /// A report is taken only from the board the sensor is on, and one for a sensor that does not exist
    /// is ignored (<c>RemoteSensor::UpdateRemoteTemperature</c>, <c>Heat::ProcessRemoteSensorsReport</c>)
    /// </remarks>
    public void ApplyRemoteReading(byte source, int sensorNumber, float temperature, TemperatureError state)
    {
        if (Find(sensorNumber) is AnalogSensor sensor && sensor.Port is not null && GetBoard(sensor) == source)
        {
            SetResult(sensorNumber, sensor, temperature, state);
        }
    }

    /// <summary>
    /// Apply the main board's MCU temperature to the sensors that read it
    /// </summary>
    /// <param name="temperature">The MCU temperature</param>
    /// <remarks>
    /// RepRapFirmware's <c>CpuTemperatureSensor::Poll</c>. The main board is the only place the reading is
    /// adjusted here, because an expansion board applies the adjustments of its own sensors
    /// </remarks>
    public void ApplyMcuTemperature(float temperature)
    {
        for (int sensorNumber = 0; sensorNumber < _model.Sensors.Analog.Count; sensorNumber++)
        {
            if (_model.Sensors.Analog[sensorNumber] is AnalogSensor { Port: null, Type: AnalogSensorType.McuTemp } sensor)
            {
                SetResult(sensorNumber, sensor, (temperature * (1.0f + sensor.SlopeAdj)) + sensor.OffsetAdj, TemperatureError.Ok);
            }
        }
    }

    /// <summary>
    /// Record a reading
    /// </summary>
    /// <remarks>RepRapFirmware's <c>TemperatureSensor::SetResult</c></remarks>
    private void SetResult(int sensorNumber, AnalogSensor sensor, float temperature, TemperatureError state)
    {
        sensor.LastReading = temperature;
        sensor.State = state;
        _whenLastRead[sensorNumber] = _timeProvider.GetTimestamp();
        if (state != TemperatureError.Ok)
        {
            _lastRealError[sensorNumber] = state;
        }
    }

    /// <summary>
    /// Time out every sensor whose last reading is older than <see cref="ReadingTimeout"/>
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>TemperatureSensor::CheckTimeout</c>: the reading becomes
    /// <see cref="HeatManager.BadErrorTemperature"/> and the state <see cref="TemperatureError.Timeout"/>
    /// </remarks>
    public void CheckTimeouts()
    {
        long now = _timeProvider.GetTimestamp();
        for (int sensorNumber = 0; sensorNumber < _model.Sensors.Analog.Count && sensorNumber < MaxSensors; sensorNumber++)
        {
            if (_model.Sensors.Analog[sensorNumber] is not AnalogSensor sensor)
            {
                continue;
            }

            // A sensor this manager did not create, such as one a client wrote into the model, is timed from
            // when it is first seen
            long whenLastRead = _whenLastRead[sensorNumber] ??= now;
            if (_timeProvider.GetElapsedTime(whenLastRead, now) > ReadingTimeout)
            {
                sensor.LastReading = HeatManager.BadErrorTemperature;
                sensor.State = TemperatureError.Timeout;
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(PollInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                using (await _model.AccessReadWriteAsync(stoppingToken))
                {
                    CheckTimeouts();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down
        }
    }
}
