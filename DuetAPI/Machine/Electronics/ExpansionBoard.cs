using System;

namespace DuetAPI.Machine.Electronics
{
    /// <summary>
    /// Represents information about an attached expansion board
    /// </summary>
    public class ExpansionBoard : ICloneable
    {
        /// <summary>
        /// Name of the attached expansion board
        /// </summary>
        public string Name { get; set; }
        
        /// <summary>
        /// Revision of the expansion board
        /// </summary>
        public string Revision { get; set; }
        
        /// <summary>
        /// Details about the firmware running on this expansion board
        /// </summary>
        public Firmware Firmware { get; set; } = new Firmware();
        
        /// <summary>
        /// Set of the minimum, maximum and current input voltage (in V or null if unknown)
        /// </summary>
        public MinMaxCurrent<double?> VIn { get; set; } = new MinMaxCurrent<double?>();
        
        /// <summary>
        /// Set of the minimum, maximum and current MCU temperature (in degC or null if unknown)
        /// </summary>
        public MinMaxCurrent<double?> McuTemp { get; set; } = new MinMaxCurrent<double?>();
        
        /// <summary>
        /// How many heaters can be attached to this board
        /// </summary>
        public int? MaxHeaters { get; set; }
        
        /// <summary>
        /// How many drives can be attached to this board
        /// </summary>
        public int? MaxMotors { get; set; }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new ExpansionBoard
            {
                Name = (Name != null) ? string.Copy(Name) : null,
                Revision = (Revision != null) ? string.Copy(Revision) : null,
                Firmware = (Firmware)Firmware.Clone(),
                VIn = (MinMaxCurrent<double?>)VIn.Clone(),
                McuTemp = McuTemp,
                MaxHeaters = MaxHeaters,
                MaxMotors = MaxMotors
            };
        }
    }
}
