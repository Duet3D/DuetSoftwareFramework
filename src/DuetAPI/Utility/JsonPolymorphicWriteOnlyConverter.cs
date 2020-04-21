﻿using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.Utility
{
    /// <summary>
    /// JSON converter for converting inherited class types to JSON
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class JsonPolymorphicWriteOnlyConverter<T> : JsonConverter<T>
    {
        /// <summary>
        /// Read from JSON
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="typeToConvert">Type to convert</param>
        /// <param name="options">Read options</param>
        /// <returns>Read value</returns>
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Write a CodeParameter to JSON
        /// </summary>
        /// <param name="writer">JSON writer</param>
        /// <param name="value">Value to serialize</param>
        /// <param name="options">Write options</param>
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, value.GetType(), JsonHelper.DefaultJsonOptionsNoConverters);
        }
    }
}
