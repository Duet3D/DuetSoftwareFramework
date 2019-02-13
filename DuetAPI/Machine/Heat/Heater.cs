using System;

namespace DuetAPI.Machine.Heat
{
    public enum HeaterState
    {
        Off,
        Standby,
        Active,
        Tuning
    }

    public class Heater : ICloneable
    {
        public double Current { get; set; }                                         // degC
        public string Name { get; set; }
        public HeaterState? State { get; set; }
        public HeaterModel Model { get; set; } = new HeaterModel();
        public double? Max { get; set; }                                            // degC
        public uint? Sensor { get; set; }                                           // thermistor index

        public object Clone()
        {
            return new Heater
            {
                Current = Current,
                Name = (Name != null) ? string.Copy(Name) : null,
                State = State,
                Model = (HeaterModel)Model.Clone(),
                Max = Max,
                Sensor = Sensor
            };
        }
    }
}