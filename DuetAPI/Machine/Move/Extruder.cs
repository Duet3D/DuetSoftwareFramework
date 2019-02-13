using System;

namespace DuetAPI.Machine.Move
{
    public class Extruder : ICloneable
    {
        public double Factor { get; set; } = 1.0;
        public ExtruderNonlinear Nonlinear { get; set; } = new ExtruderNonlinear();

        public object Clone()
        {
            return new Extruder
            {
                Factor = Factor,
                Nonlinear = (ExtruderNonlinear)Nonlinear.Clone()
            };
        }
    }
}