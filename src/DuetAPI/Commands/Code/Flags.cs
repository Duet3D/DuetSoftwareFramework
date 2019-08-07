using Newtonsoft.Json;
using System;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Code bits to classify G/M/T-codes
    /// </summary>
    [Flags]
    [JsonConverter(typeof(CodeFlagsConverter))]
    public enum CodeFlags : int
    {
        /// <summary>
        /// Placeholder to indicate that no flags are set
        /// </summary>
        None = 0,

        /// <summary>
        /// Code execution finishes as soon as it is enqueued in the RRF transmit buffer
        /// </summary>
        /// <remarks>
        /// Potential G-code replies from RRF are only reported through the object model.
        /// This behaviour will be enhanced in the future.
        /// </remarks>
        Asynchronous = 1,

        /// <summary>
        /// Code has been preprocessed (i.e. it has been processed by the DCS pre-side code interceptors)
        /// </summary>
        IsPreProcessed = 2,

        /// <summary>
        /// Code has been postprocessed (i.e. it has been processed by the internal DCS code processor)
        /// </summary>
        IsPostProcessed = 4,

        /// <summary>
        /// Code originates from a macro file
        /// </summary>
        IsFromMacro = 8,

        /// <summary>
        /// Code originates from a system macro file (i.e. RRF requested it)
        /// </summary>
        IsNestedMacro = 16,

        /// <summary>
        /// Code comes from config.g or config.g.bak
        /// </summary>
        IsFromConfig = 32,

        /// <summary>
        /// Code comes from config-override.g
        /// </summary>
        IsFromConfigOverride = 64,

        /// <summary>
        /// Enforce absolute positioning via prefixed G53 code
        /// </summary>
        EnforceAbsolutePosition = 128,

        /// <summary>
        /// Override every other code and send it to the firmware as quickly as possible
        /// </summary>
        IsPrioritized = 256
    }

    /// <summary>
    /// Class to convert a <see cref="CodeFlags"/> instance to an int
    /// </summary>
    public class CodeFlagsConverter : JsonConverter
    {
        /// <summary>
        /// Write code flags to JSON
        /// </summary>
        /// <param name="writer">JSON writer</param>
        /// <param name="value">Code flag value</param>
        /// <param name="serializer">JON serializer</param>
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            writer.WriteValue((int)value);
        }

        /// <summary>
        /// Read code flags from JSON
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="objectType">Object type</param>
        /// <param name="existingValue">Existing value</param>
        /// <param name="serializer">JSON serializer</param>
        /// <returns>Deserialized code flags</returns>
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            return (CodeFlags)(int)reader.Value;
        }

        /// <summary>
        /// Check if the type can be converted
        /// </summary>
        /// <param name="objectType">Object type</param>
        /// <returns>Whether the type can be converted</returns>
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(int);
        }
    }
}
