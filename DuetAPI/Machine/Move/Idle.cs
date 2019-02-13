using System;

namespace DuetAPI.Machine.Move
{
    public class Idle : ICloneable
    {
        public double Timeout { get; set; } = 30;           // seconds
        public double Factor { get; set; } = 0.3;           // per cent

        public object Clone()
        {
            return new Idle
            {
                Timeout = Timeout,
                Factor = Factor
            };
        }
    }
}