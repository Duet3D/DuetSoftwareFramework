using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.Utility;

/// <summary>
/// Class for easier access to JsonStringEnumConverter with lower-case naming.
/// Use this for enums whose members mix upper-case letters and digits (e.g. EI2), because the camel-case policy would only lower the first letter of those
/// </summary>
public class JsonLowerCaseStringEnumConverter<TEnum> : JsonStringEnumConverter<TEnum> where TEnum : struct, Enum
{
    /// <summary>
    /// Constructor of this class
    /// </summary>
    public JsonLowerCaseStringEnumConverter() : base(LowerCaseNamingPolicy.Instance) { }

    /// <summary>
    /// Naming policy that lowers every character
    /// </summary>
    private sealed class LowerCaseNamingPolicy : JsonNamingPolicy
    {
        /// <summary>
        /// Shared instance
        /// </summary>
        public static readonly LowerCaseNamingPolicy Instance = new();

        /// <summary>
        /// Convert a member name
        /// </summary>
        /// <param name="name">Member name</param>
        /// <returns>Lower-case name</returns>
        public override string ConvertName(string name) => name.ToLowerInvariant();
    }
}
