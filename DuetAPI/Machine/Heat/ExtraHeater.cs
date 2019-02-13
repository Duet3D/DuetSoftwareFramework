using System;

namespace DuetAPI.Machine.Heat
{
    public class ExtraHeater : ICloneable
    {
        public double Current { get; set; }         // degC
        public string Name { get; set; }
        public HeaterState? State { get; set; }
        public uint? Sensor { get; set; }           // thermistor index

        public object Clone()
        {
            return new ExtraHeater
            {
                Current = Current,
                Name = (Name != null) ? string.Copy(Name) : null,
                State = State,
                Sensor = Sensor
            };
        }
    }
}