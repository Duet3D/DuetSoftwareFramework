using System;

namespace DuetAPI.Machine.Job
{
    public class TimesLeft : ICloneable
    {
        public double? File { get; set; }           // seconds
        public double? Filament { get; set; }       // seconds
        public double? Layer { get; set; }          // seconds

        public object Clone()
        {
            return new TimesLeft
            {
                File = File,
                Filament = Filament,
                Layer = Layer
            };
        }
    }
}