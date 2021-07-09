using System;
using System.ComponentModel;
using System.Text.Json;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Basic interface for object model classes that have properties
    /// </summary>
    public interface IModelObject : ICloneable, INotifyPropertyChanged
    {
        /// <summary>
        /// Assign the properties from another instance.
        /// This is required to update model properties which do not have a setter
        /// </summary>
        /// <param name="from">Other instance</param>
        void Assign(object from);

        /// <summary>
        /// Create a dictionary or list of all the differences between this instance and another.
        /// This method outputs own property values that differ from the other instance
        /// </summary>
        /// <param name="other">Other instance</param>
        /// <returns>Object differences or null if both instances are equal</returns>
        object FindDifferences(IModelObject other);

        /// <summary>
        /// Update this instance from a given JSON element
        /// </summary>
        /// <param name="jsonElement">Element to update this intance from</param>
        /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
        /// <returns>Updated instance</returns>
        /// <exception cref="JsonException">Failed to deserialize data</exception>
        IModelObject UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties);
    }
}
