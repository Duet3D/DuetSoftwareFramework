using System;

namespace DuetAPI.Machine.Spindles
{
    public class Spindle : ICloneable
    {
        public double Active { get; set; }              // RPM
        public double Current { get; set; }             // RPM

        public object Clone()
        {
            return new Spindle
            {
                Active = Active,
                Current = Current
            };
        }
    }
}