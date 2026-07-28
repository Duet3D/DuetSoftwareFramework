using System;
using System.Collections.Generic;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Default implementation of <see cref="IModelObjectDescriptor"/> holding a fixed set of property descriptors
/// </summary>
public sealed class ModelObjectDescriptor : IModelObjectDescriptor
{
    private readonly Dictionary<string, ModelPropertyDescriptor> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModelPropertyDescriptor> _byNameIgnoreCase = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelPropertyDescriptor> _byJsonName = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a new type descriptor from the given property descriptors
    /// </summary>
    /// <param name="properties">Property descriptors in declaration order</param>
    public ModelObjectDescriptor(params ModelPropertyDescriptor[] properties)
    {
        Properties = properties;
        foreach (ModelPropertyDescriptor property in properties)
        {
            _byName[property.Name] = property;
            _byNameIgnoreCase[property.Name] = property;
            _byJsonName[property.JsonName] = property;
        }
    }

    /// <summary>
    /// Descriptors of all properties of this type in declaration order
    /// </summary>
    public IReadOnlyList<ModelPropertyDescriptor> Properties { get; }

    /// <summary>
    /// Look up a property by its name
    /// </summary>
    /// <param name="name">Name of the property</param>
    /// <param name="ignoreCase">Whether to perform a case-insensitive lookup</param>
    /// <returns>Property descriptor or null if not found</returns>
    public ModelPropertyDescriptor? FindProperty(string name, bool ignoreCase)
    {
        return (ignoreCase ? _byNameIgnoreCase : _byName).TryGetValue(name, out ModelPropertyDescriptor? property) ? property : null;
    }

    /// <summary>
    /// Look up a property by its JSON name
    /// </summary>
    /// <param name="jsonName">JSON name of the property</param>
    /// <returns>Property descriptor or null if not found</returns>
    public ModelPropertyDescriptor? FindPropertyByJsonName(string jsonName)
    {
        return _byJsonName.TryGetValue(jsonName, out ModelPropertyDescriptor? property) ? property : null;
    }
}
