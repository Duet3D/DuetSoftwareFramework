using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.Utility
{
    /// <summary>
    /// Class for easier access to JsonStringEnumConverter with camel-case naming
    /// </summary>
    public class JsonCamelCaseStringEnumConverter<TEnum> : JsonStringEnumConverter<TEnum> where TEnum : struct, Enum
    {
        /// <summary>
        /// Constructor of this class
        /// </summary>
        public JsonCamelCaseStringEnumConverter() : base(JsonNamingPolicy.CamelCase) { }
    }
}
