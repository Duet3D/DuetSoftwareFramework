using System;

namespace DuetAPI.Machine.Storages
{
    /// <summary>
    /// Information about a storage device
    /// </summary>
    public class Storage : ICloneable
    {
        /// <summary>
        /// Whether the storage device is mounted
        /// </summary>
        public bool Mounted { get; set; }
        
        /// <summary>
        /// Speed of the storage device (in bytes/s or null if unknown)
        /// </summary>
        public uint? Speed { get; set; }
        
        /// <summary>
        /// Total capacity of the storage device (in bytes)
        /// </summary>
        public ulong? Capacity { get; set; }
        
        /// <summary>
        /// How much space is still available on this device (in bytes)
        /// </summary>
        public ulong? Free { get; set; }
        
        /// <summary>
        /// Number of currently open files or null if unknown
        /// </summary>
        public uint? OpenFiles { get; set; }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
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