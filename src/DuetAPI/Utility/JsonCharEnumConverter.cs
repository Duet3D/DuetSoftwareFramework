using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.Utility
{
    /// <summary>
    /// Converter factory for converting enum types to char
    /// </summary>
    public class JsonCharEnumConverter : JsonConverterFactory
    {
        /// <summary>
        /// Checks if the given type can be converted
        /// </summary>
        /// <param name="typeToConvert">Type to convert</param>
        /// <returns>If the type can be converted</returns>
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        /// <summary>
        /// Creates a converter for the given type
        /// </summary>
        /// <param name="type"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options)
        {
            Type converterType = typeof(JsonCharEnumConverterInner<>).MakeGenericType(type);
            return (JsonConverter)Activator.CreateInstance(converterType);
        }

        /// <summary>
        /// Inner converter for char to enum conversions
        /// </summary>
        /// <typeparam name="T"></typeparam>
        private class JsonCharEnumConverterInner<T> : JsonConverter<T> where T : Enum
        {
            /// <summary>
            /// Checks if the given type can be converted
            /// </summary>
            /// <param name="typeToConvert">Type to convert</param>
            /// <returns>Whether the type can be converted</returns>
            public override bool CanConvert(Type typeToConvert)
            {
                return typeToConvert.IsEnum;
            }

            /// <summary>
            /// Read from JSON
            /// </summary>
            /// <param name="reader">JSON reader</param>
            /// <param name="typeToConvert">Type to convert</param>
            /// <param name="options">Read options</param>
            /// <returns>Read value</returns>
            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                return (T)(object)Convert.ToInt32(Convert.ToChar(reader.GetString()));
            }

            /// <summary>
            /// Write a CodeParameter to JSON
            /// </summary>
            /// <param name="writer">JSON writer</param>
            /// <param name="value">Value to serialize</param>
            /// <param name="options">Write options</param>
            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            {
                writer.WriteStringValue(Convert.ToChar(value).ToString());
            }
        }
    }
}
