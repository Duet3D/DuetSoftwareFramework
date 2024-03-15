using System;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Attribute used to mark properties that provide only a limited number of items in a standard model response
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class LimitedResponseCountAttribute : Attribute
    {
        /// <summary>
        /// Indicates how many items are included max in a standard response
        /// </summary>
        public int MaxCount { get; set; }

        /// <summary>
        /// Constructor of this attribute
        /// </summary>
        /// <param name="maxCount">Maximum number of items reported by default</param>
        public LimitedResponseCountAttribute(int maxCount)
        {
            MaxCount = maxCount;
        }
    }
}
