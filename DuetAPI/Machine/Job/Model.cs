using System;
using System.Collections.Generic;
using System.Linq;

namespace DuetAPI.Machine.Job
{
    public class Model : ICloneable
    {
        public File File { get; set; } = new File();
        public long? FilePosition { get; set; }                                     // bytes
        public string LastFileName { get; set; }
        public bool LastFileSimulated { get; set; }
        public double[] ExtrudedRaw { get; set; } = new double[0];                  // mm
        public double? Duration { get; set; }                                       // seconds
        public uint? Layer { get; set; }
        public double? LayerTime { get; set; }                                      // seconds
        public List<Layer> Layers { get; set; } = new List<Layer>();
        public double? WarmUpDuration { get; set; }                                 // seconds
        public TimesLeft TimesLeft { get; set; } = new TimesLeft();

        public object Clone()
        {
            return new Model
            {
                File = (File)File.Clone(),
                FilePosition = FilePosition,
                LastFileName = (LastFileName != null) ? string.Copy(LastFileName) : null,
                LastFileSimulated = LastFileSimulated,
                ExtrudedRaw = (double[])ExtrudedRaw.Clone(),
                Duration = Duration,
                Layer = Layer,
                LayerTime = LayerTime,
                Layers = Layers.Select(layer => (Layer)layer.Clone()).ToList(),
                WarmUpDuration = WarmUpDuration,
                TimesLeft = (TimesLeft)TimesLeft.Clone()
            };
        }
    }
}
