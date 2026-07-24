namespace DuetAPI.ObjectModel;

/// <summary>
/// Reflection-free access to the properties of an object model instance
/// </summary>
public interface IModelObjectAccessor
{
    /// <summary>
    /// Static description of this instance's type
    /// </summary>
    IModelObjectDescriptor Descriptor { get; }

    /// <summary>
    /// Read the value of the property with the given index
    /// </summary>
    /// <param name="index">Index of the property, see <see cref="ModelPropertyDescriptor.Index"/></param>
    /// <returns>Property value</returns>
    object? GetPropertyValue(int index);
}
