using System;

namespace DuetAPI.Machine.Job
{
    public class File : ICloneable
    {
        public string Name { get; set; }
        public ulong? Size { get; set; }                                    // bytes
        public double[] FilamentNeeded { get; set; } = new double[0];       // mm
        public string GeneratedBy { get; set; }
        public double? Height { get; set; }
        public double? LayerHeight { get; set; }
        public uint? NumLayers { get; set; }
        public double? PrintTime { get; set; }                              // seconds
        public double? SimulatedTime { get; set; }                          // seconds

        public object Clone()
        {
            return new File
            {
                Name = (Name != null) ? string.Copy(Name) : null,
                Size = Size,
                FilamentNeeded = (double[])FilamentNeeded.Clone(),
                GeneratedBy = (GeneratedBy != null) ? string.Copy(GeneratedBy) : null,
                Height = Height,
                LayerHeight = LayerHeight,
                NumLayers = NumLayers,
                PrintTime = PrintTime,
                SimulatedTime = SimulatedTime
            };
        }
    }
}