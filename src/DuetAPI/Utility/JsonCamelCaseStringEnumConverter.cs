using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.Utility
{
    /// <summary>
    /// Class for easier access to JsonStringEnumConverter with camel-case naming
    /// </summary>
    public sealed class JsonCamelCaseStringEnumConverter : JsonConverterFactory
    {
        /// <summary>
        /// Converter factory for creating new camel-case converters
        /// </summary>
        private readonly JsonStringEnumConverter _converter;

        /// <summary>
        /// Creates a new instance
        /// </summary>
        public JsonCamelCaseStringEnumConverter()
        {
            _converter = new JsonStringEnumConverter(JsonNamingPolicy.CamelCase);
        }

        /// <summary>
        /// Checks if the given type can be converted
        /// </summary>
        /// <param name="typeToConvert">Type to convert</param>
        /// <returns>Whether the type can be converted</returns>
        public override bool CanConvert(Type typeToConvert) => _converter.CanConvert(typeToConvert);

        /// <summary>
        /// Creates a new JSON converter
        /// </summary>
        /// <param name="typeToConvert">Type to convert</param>
        /// <param name="options">Conversion options</param>
        /// <returns>JSON converter</returns>
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            return _converter.CreateConverter(typeToConvert, options);
        }
    }
}
