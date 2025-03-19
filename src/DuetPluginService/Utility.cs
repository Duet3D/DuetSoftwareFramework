
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using DuetAPI.Utility;

namespace DuetPluginService;

/// <summary>
/// Generic utility functions
/// </summary>
public static partial class Utility
{
    /// <summary>
    /// Application version
    /// </summary>
    public static readonly string Version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    /// <summary>
    /// Populate an existing object from the properites of the given JSON element
    /// </summary>
    /// <param name="obj">Object to populate</param>
    /// <param name="jsonElement">JSON element</param>
    public static void PopulateObject(object obj, JsonElement jsonElement)
    {
        foreach (JsonProperty property in jsonElement.EnumerateObject())
        {
            PropertyInfo? propertyInfo = obj.GetType().GetProperty(property.Name, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            if (propertyInfo != null && propertyInfo.CanWrite)
            {
                object? value = JsonSerializer.Deserialize(property.Value.GetRawText(), propertyInfo.PropertyType, JsonHelper.DefaultJsonOptions);
                propertyInfo.SetValue(obj, value);
            }
        }
    }

}