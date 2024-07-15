using System;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Attribute used to mark properties that provide only a limited number of items in a standard model response
    /// </summary>
    /// <param name="maxCount">Maximum number of items reported by default</param>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class LimitedResponseCountAttribute(int maxCount) : Attribute
    {
        /// <summary>
        /// Indicates how many items are included max in a standard response
        /// </summary>
        public int MaxCount { get; set; } = maxCount;
    }
}
