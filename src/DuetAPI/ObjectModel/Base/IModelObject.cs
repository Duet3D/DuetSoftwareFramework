using System;
using System.ComponentModel;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Basic interface for object model classes that have properties
    /// </summary>
    public interface IModelObject : ICloneable, INotifyPropertyChanged
    {
#if false
        /// <summary>
        /// Create a dictionary or list of all the differences between this instance and another.
        /// This method outputs own property values that differ from the other instance
        /// </summary>
        /// <param name="other">Other instance</param>
        /// <returns>Object differences or null if both instances are equal</returns>
        object? FindDifferences(IStaticModelObject other);
#endif
    }
}
