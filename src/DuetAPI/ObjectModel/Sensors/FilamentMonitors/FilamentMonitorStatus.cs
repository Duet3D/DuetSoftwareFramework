using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Possible filament sensor status
    /// </summary>
    [JsonConverter(typeof(JsonCamelCaseStringEnumConverter))]
    public enum FilamentMonitorStatus
    {
        /// <summary>
        /// No monitor is present
        /// </summary>
        NoMonitor,

        /// <summary>
        /// Filament working normally
        /// </summary>
        Ok,

        /// <summary>
        /// No data received from the remote filament senosr
        /// </summary>
        NoDataReceived,

        /// <summary>
        /// No filament present
        /// </summary>
        NoFilament,

        /// <summary>
        /// Sensor reads less movement than expected
        /// </summary>
        TooLittleMovement,

        /// <summary>
        /// Sensor reads more movment than expected
        /// </summary>
        TooMuchMovement,

        /// <summary>
        /// Sensor encountered an error
        /// </summary>
        SensorError
    }
}