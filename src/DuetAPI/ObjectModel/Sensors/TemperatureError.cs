using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Result codes returned by temperature sensor drivers
/// </summary>
[JsonConverter(typeof(JsonCamelCaseStringEnumConverter<TemperatureError>))]
public enum TemperatureError
{
    /// <summary>
    /// Sensor is functional
    /// </summary>
    Ok,

    /// <summary>
    /// Short circuit detected
    /// </summary>
    ShortCircuit,

    /// <summary>
    /// Short to VCC detected
    /// </summary>
    ShortToVcc,

    /// <summary>
    /// Short to GND detected
    /// </summary>
    ShortToGround,

    /// <summary>
    /// Sensor circuit is open
    /// </summary>
    OpenCircuit,

    /// <summary>
    /// Timeout while waiting for sensor data
    /// </summary>
    Timeout,

    /// <summary>
    /// IO error
    /// </summary>
    IoError,

    /// <summary>
    /// Hardware error
    /// </summary>
    HardwareError,

    /// <summary>
    /// Not ready
    /// </summary>
    NotReady,

    /// <summary>
    /// Invalid output number
    /// </summary>
    InvalidOutputNumber,
    
    /// <summary>
    /// Sensor bus is busy
    /// </summary>
    BusBusy,

    /// <summary>
    /// Bad sensor response
    /// </summary>
    BadResponse,

    /// <summary>
    /// Unknown sensor port
    /// </summary>
    UnknownPort,

    /// <summary>
    /// Sensor not initialized
    /// </summary>
    NotInitialised,

    /// <summary>
    /// Unknown sensor
    /// </summary>
    UnknownSensor,

    /// <summary>
    /// Sensor exceeded min/max voltage
    /// </summary>
    OverOrUnderVoltage,

    /// <summary>
    /// Bad VREF detected
    /// </summary>
    BadVref,

    /// <summary>
    /// Bad VSSA detected
    /// </summary>
    BadVssa,

    /// <summary>
    /// Sensor reading too low
    /// </summary>
    ReadingTooLow,

    /// <summary>
    /// Sensor reading too high
    /// </summary>
    ReadingTooHigh,

    /// <summary>
    /// Ambient reading too low (for composite sensors that read ambient temperature to calculate object temperature)
    /// </summary>
    AmbientReadingTooLow,

    /// <summary>
    /// Ambient reading too high (for composite sensors that read ambient temperature to calculate object temperature)
    /// </summary>
    AmbientReadingTooHigh,

    /// <summary>
    /// Unknown error
    /// </summary>
    UnknownError
}
