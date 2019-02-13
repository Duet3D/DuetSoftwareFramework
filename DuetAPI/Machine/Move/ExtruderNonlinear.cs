using System;

namespace DuetAPI.Machine.Move
{
    public class ExtruderNonlinear : ICloneable
    {
        public double A { get; set; }
        public double B { get; set; }
        public double UpperLimit { get; set; } = 0.2;
        public double Temperature { get; set; }

        public object Clone()
        {
            return new ExtruderNonlinear
            {
                A = A,
                B = B,
                UpperLimit = UpperLimit,
                Temperature = Temperature
            };
        }
    }
}