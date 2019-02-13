using System;

namespace DuetAPI.Machine.Job
{
    public class Layer : ICloneable
    {
        public double? Duration { get; set; }                                   // seconds
        public double? Height { get; set; }                                     // mm
        public double[] Filament { get; set; } = new double[0];                 // mm
        public double? FractionPrinted { get; set; }

        public object Clone()
        {
            return new Layer
            {
                Duration = Duration,
                Height = Height,
                Filament = (double[])Filament.Clone(),
                FractionPrinted = FractionPrinted
            };
        }
    }
}