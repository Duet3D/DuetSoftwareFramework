using System;

namespace DuetAPI.Machine.Electronics
{
    /// <summary>
    /// Information about a firmware version
    /// </summary>
    public class Firmware : ICloneable
    {
        /// <summary>
        /// Name of the firmware
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Version of the firmware
        /// </summary>
        public string Version { get; set; }

        /// <summary>
        /// Date of the firmware (i.e. when it was compiled)
        /// </summary>
        public string Date { get; set; }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Firmware
            {
                Name = (Name != null) ? string.Copy(Name) : null,
                Version = (Version != null) ? string.Copy(Version) : null,
                Date = (Date != null) ? string.Copy(Date) : null
            };
        }
    }
}
