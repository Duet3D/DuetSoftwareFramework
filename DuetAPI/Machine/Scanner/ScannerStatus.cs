using DuetAPI.Utility;
using Newtonsoft.Json;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Possible states of the attached 3D scanner
    /// </summary>
    [JsonConverter(typeof(CharEnumConverter))]
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
