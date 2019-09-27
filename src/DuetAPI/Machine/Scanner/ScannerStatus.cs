using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Possible states of the attached 3D scanner
    /// </summary>
    [JsonConverter(typeof(JsonCharEnumConverter))]
    public enum ScannerStatus
    {
        /// <summary>
        /// Scanner is disconnected (none present)
        /// </summary>
        Disconnected = 'D',

        /// <summary>
        /// Scanner is registered and idle
        /// </summary>
        Idle = 'I',

        /// <summary>
        /// Scanner is scanning an object
        /// </summary>
        Scanning = 'S',

        /// <summary>
        /// Scanner is post-processing a file
        /// </summary>
        PostProcessing = 'P',

        /// <summary>
        /// Scanner is calibrating
        /// </summary>
        Calibrating = 'C',

        /// <summary>
        /// Scanner is uploading
        /// </summary>
        Uploading = 'U'
    }
}
