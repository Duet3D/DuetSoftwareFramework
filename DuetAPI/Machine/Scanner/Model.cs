using System;
using Newtonsoft.Json;

namespace DuetAPI.Machine.Scanner
{
    /// <summary>
    /// Possible states of the attached 3D scanner
    /// </summary>
    [JsonConverter(typeof(CharEnumConverter))]
    public enum ScannerStatus
    {
        Disconnected = 'D',
        Idle = 'I',
        Scanning = 'S',
        PostProcessing = 'P',
        Calibrating = 'C',
        Uploading = 'U'
    }

    /// <summary>
    /// Information about the 3D scanner subsystem
    /// </summary>
    public class Model : ICloneable
    {
        /// <summary>
        /// Progress of the current action (on a scale between 0 to 1)
        /// </summary>
        /// <remarks>
        /// Previous status responses used a scale of 0..100
        /// </remarks>
        public double Progress { get; set; }
        
        /// <summary>
        /// Status of the 3D scanner
        /// </summary>
        /// <seealso cref="ScannerStatus"/>
        public ScannerStatus Status { get; set; } = ScannerStatus.Disconnected;

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Model
            {
                Progress = Progress,
                Status = Status
            };
        }
    }
}