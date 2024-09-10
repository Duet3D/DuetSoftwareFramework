using System.Text.Json;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Dynamic interface for object model classes that have properties.
    /// Classes that implement this interface may change its instance on update
    /// </summary>
    public interface IDynamicModelObject : IModelObject
    {
        /// <summary>
        /// Assign the properties from another instance.
        /// This is required to update model properties which do not have a setter
        /// </summary>
        /// <param name="from">Other instance</param>
        IDynamicModelObject? Assign(IDynamicModelObject from);

        /// <summary>
        /// Update this instance from a given JSON element
        /// </summary>
        /// <param name="jsonElement">Element to update this intance from</param>
        /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
        /// <returns>Updated instance</returns>
        /// <exception cref="JsonException">Failed to deserialize data</exception>
        IDynamicModelObject? UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties);

        /// <summary>
        /// Update this instance from a given JSON reader
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
        /// <returns>Updated instance</returns>
        /// <exception cref="JsonException">Failed to deserialize data</exception>
        IDynamicModelObject? UpdateFromJsonReader(ref Utf8JsonReader reader, bool ignoreSbcProperties);
    }
}
