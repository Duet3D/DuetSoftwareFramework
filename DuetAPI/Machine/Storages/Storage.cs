using System;

namespace DuetAPI.Machine.Storages
{
    public class Storage : ICloneable
    {
        public bool Mounted { get; set; }
        public uint? Speed { get; set; }                // bytes/s
        public ulong? Capacity { get; set; }            // bytes
        public ulong? Free { get; set; }                // bytes
        public uint? OpenFiles { get; set; }

        public object Clone()
        {
            return new Storage
            {
                Mounted = Mounted,
                Speed = Speed,
                Capacity = Capacity,
                Free = Free,
                OpenFiles = OpenFiles
            };
        }
    }
}