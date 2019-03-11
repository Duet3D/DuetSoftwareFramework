using System;

namespace DuetAPI.Machine.Electronics
{
    /// <summary>
    /// Set holding minimum, maximum and current information
    /// </summary>
    /// <typeparam name="T">Type of each value</typeparam>
    public class MinMaxCurrent<T> : ICloneable
    {
        /// <summary>
        /// Current value
        /// </summary>
        public T Current { get; set; }

        /// <summary>
        /// Minimum value
        /// </summary>
        public T Min { get; set; }

        /// <summary>
        /// Maximum value
        /// </summary>
        public T Max { get; set; }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new MinMaxCurrent<T>
            {
                Current = Current,
                Min = Min,
                Max = Max
            };
        }
    }
}
