using Newtonsoft.Json;
using System;

namespace DuetAPI.Utility
{
    /// <summary>
    /// Helper class for Newtonsoft.Json to convert char enums to strings and vice versa
    /// </summary>
    public class CharEnumConverter : JsonConverter
    {
        /// <summary>
        /// Checks if the object can be converted
        /// </summary>
        /// <param name="objectType">Object type to check</param>
        /// <returns>Whether the object can be converted</returns>
        public override bool CanConvert(Type objectType)
        {
            return objectType.IsEnum;
        }

        /// <summary>
        /// Writes a char enum to JSON
        /// </summary>
        /// <param name="writer">JSON writer</param>
        /// <param name="serializer">JSON Serializer</param>
        /// <param name="value">Value to write</param>
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            char asChar = (char)(int)value;
            writer.WriteValue(asChar.ToString());
        }

        /// <summary>
        /// Reads a char enum from JSON
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="objectType">Object type</param>
        /// <param name="existingValue">Existing value</param>
        /// <param name="serializer">JSON serializer</param>
        /// <returns>Deserialized char enum</returns>
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            string value = (string)reader.Value;
            return (value?.Length == 1) ? (int)value[0] : existingValue;
        }
    }
}
