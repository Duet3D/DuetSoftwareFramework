using System;

namespace DuetAPI.Machine.Electronics
{
    public class ExpansionBoard : ICloneable
    {
        public string Name { get; set; }
        public string Revision { get; set; }
        public Firmware Firmware { get; set; } = new Firmware();
        public MinMaxCurrent<double?> VIn { get; set; } = new MinMaxCurrent<double?>();
        public MinMaxCurrent<double?> McuTemp { get; set; } = new MinMaxCurrent<double?>();
        public uint? MaxHeaters { get; set; }
        public uint? MaxMotors { get; set; }

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
