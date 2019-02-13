using System;

namespace DuetAPI.Machine.Heat
{
    public class HeaterModel : ICloneable
    {
        public double? Gain { get; set; }
        public double? TimeConst { get; set; }
        public double? DeadTime { get; set; }
        public double? MaxPwm { get; set; }

        public object Clone()
        {
            return new HeaterModel
            {
                Gain = Gain,
                TimeConst = TimeConst,
                DeadTime = DeadTime,
                MaxPwm = MaxPwm
            };
        }
    }
}