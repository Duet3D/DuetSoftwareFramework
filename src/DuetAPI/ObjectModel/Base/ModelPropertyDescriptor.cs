using System;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Static description of a single object model property
/// </summary>
public sealed class ModelPropertyDescriptor
{
    private readonly Func<IModelObjectDescriptor>? _elementDescriptor;

    /// <summary>
    /// Creates a new property descriptor
    /// </summary>
    /// <param name="index">Index of the property within its declaring type</param>
    /// <param name="name">Name of the property</param>
    /// <param name="jsonName">JSON name of the property</param>
    /// <param name="kind">Kind of the property</param>
    /// <param name="flags">Attributes of the property</param>
    /// <param name="elementDescriptor">Descriptor of the nested model type or collection item type</param>
    public ModelPropertyDescriptor(int index, string name, string jsonName, ModelPropertyKind kind, ModelPropertyFlags flags, Func<IModelObjectDescriptor>? elementDescriptor = null)
    {
        Index = index;
        Name = name;
        JsonName = jsonName;
        Kind = kind;
        Flags = flags;
        _elementDescriptor = elementDescriptor;
    }

    /// <summary>
    /// Index of this property within the declaring type, see <see cref="IModelObjectAccessor.GetPropertyValue"/>
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// Name of this property
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// JSON name of this property
    /// </summary>
    public string JsonName { get; }

    /// <summary>
    /// Kind of this property
    /// </summary>
    public ModelPropertyKind Kind { get; }

    /// <summary>
    /// Attributes of this property
    /// </summary>
    public ModelPropertyFlags Flags { get; }

    /// <summary>
    /// Descriptor of the nested model type or of the item type for collections and dictionaries, else null.
    /// This is resolved on demand because the object model graph may contain cycles
    /// </summary>
    public IModelObjectDescriptor? ElementDescriptor => _elementDescriptor?.Invoke();
}
