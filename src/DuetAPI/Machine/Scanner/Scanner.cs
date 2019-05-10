using System;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the 3D scanner subsystem
    /// </summary>
    public class Scanner : ICloneable
    {
        /// <summary>
        /// Progress of the current action (on a scale between 0 to 1)
        /// </summary>
        /// <remarks>
        /// Previous status responses used a scale of 0..100
        /// </remarks>
        public float Progress { get; set; }
        
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
            return new Scanner
            {
                Progress = Progress,
                Status = Status
            };
        }
    }
}