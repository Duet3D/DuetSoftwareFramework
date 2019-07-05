using Newtonsoft.Json;
using System;

namespace DuetAPI.Commands
{
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
        EnforceAbsolutePosition = 128
    }

    /// <summary>
    /// Class to convert a <see cref="CodeFlags"/> instance to an int
    /// </summary>
    public class CodeFlagsConverter : JsonConverter
    {
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            writer.WriteValue((int)value);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            return (CodeFlags)(int)reader.Value;
        }

        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(int);
        }
    }
}
