using Newtonsoft.Json;
using System;

namespace DuetAPI.Commands
{
    public class FileInfoResult
    {
        public string FileName { get; set; }
        public long Size { get; set; }
        public DateTime? LastModified { get; set; }
        public double Height { get; set; }
        public double FirstLayerHeight { get; set; }
        public double LayerHeight { get; set; }
        public double[] Filament { get; set; } = new double[0];
        public string GeneratedBy { get; set; }
        public double PrintTime { get; set; }
        public double SimulatedTime { get; set; }
    }
}