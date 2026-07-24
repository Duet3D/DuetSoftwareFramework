using System.Collections.Generic;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Static description of an object model type. Instances of this are generated per model type and shared,
/// so they can be walked without instantiating the types they describe
/// </summary>
public interface IModelObjectDescriptor
{
    /// <summary>
    /// Descriptors of all properties of this type in declaration order
    /// </summary>
    IReadOnlyList<ModelPropertyDescriptor> Properties { get; }

    /// <summary>
    /// Look up a property by its name
    /// </summary>
    /// <param name="name">Name of the property</param>
    /// <param name="ignoreCase">Whether to perform a case-insensitive lookup</param>
    /// <returns>Property descriptor or null if not found</returns>
    ModelPropertyDescriptor? FindProperty(string name, bool ignoreCase);

    /// <summary>
    /// Look up a property by its JSON name
    /// </summary>
    /// <param name="jsonName">JSON name of the property</param>
    /// <returns>Property descriptor or null if not found</returns>
    ModelPropertyDescriptor? FindPropertyByJsonName(string jsonName);
}
