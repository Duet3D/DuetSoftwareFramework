using System;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Represents a parsed parameter of a G/M/T-code
    /// Initial parsing is done whenever a code is processed
    /// </summary>
    [JsonConverter(typeof(CodeParameterConverter))]
    public class CodeParameter
    {
        /// <summary>
        /// Letter of the code parameter (e.g. P in M106 P3)
        /// </summary>
        public char Letter { get; }

        /// <summary>
        /// Unparsed string representation of the code parameter or an empty string if none present
        /// </summary>
        public string AsString { get; }

        /// <summary>
        /// Internal parsed representation of the string value (one of string, int, uint, float, int[], uint[] or float[])
        /// </summary>
        private readonly object ParsedValue;
        
        /// <summary>
        /// Creates a new CodeParameter instance and parses value to a native data type if applicable
        /// </summary>
        /// <param name="letter">Letter of the code parameter</param>
        /// <param name="value">String representation of the value (also stored in <see cref="AsString"/>)</param>
        /// <param name="isString">Whether this is a string. This is set to true if the parameter was inside quotation marks.</param>
        public CodeParameter(char letter, string value, bool isString)
        {
            Letter = letter;
            if (isString)
            {
                // Value is definitely a string because it is encapsulated in quotation marks
                AsString = value;
                ParsedValue = value;
            }
            else
            {
                // It is not encapsulated...
                value = value.Trim();
                AsString = value;
                
                // If it contains colons, it is most likely an array
                if (value.Contains(':'))
                {
                    string[] subArgs = value.Split(':');
                    try
                    {
                        if (value.Contains('.'))
                        {
                            // If there is a dot anywhere, attempt to parse it as a float array
                            ParsedValue = subArgs.Select(subArg => float.Parse(subArg, NumberStyles.Any, CultureInfo.InvariantCulture)).ToArray();
                        }
                        else
                        {
                            try
                            {
                                // If there is no dot, it could be an integer array
                                ParsedValue = subArgs.Select(int.Parse);
                            }
                            catch
                            {
                                // If that failed, attempt to parse everything as a uint array
                                ParsedValue = subArgs.Select(uint.Parse);
                            }
                        }
                    }
                    catch
                    {
                        // It must be a string (fallback)
                        ParsedValue = value;
                    }
                }
                else if (int.TryParse(value, out int asInt))
                {
                    // It is a valid integer
                    ParsedValue = asInt;
                }
                else if (uint.TryParse(value, out uint asUInt))
                {
                    // It is a valid long
                    ParsedValue = asUInt;
                }
                else if (float.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out float asFloat))
                {
                    // It is a valid float
                    ParsedValue = asFloat;
                }
                else
                {
                    // It must be a string (fallback)
                    ParsedValue = value;
                }
            }
        }
        /// <summary>
        /// Float representation of the parsed value
        /// </summary>
        /// <exception cref="ArgumentException">Data type is not convertible</exception>
        [JsonIgnore]
        public float AsFloat
        {
            get
            {
                if (ParsedValue is int || ParsedValue is float)
                {
                    return Convert.ToSingle(ParsedValue);
                }
                
                throw new ArgumentException($"Cannot convert {Letter} parameter to float (value {AsString})");
            }
        }
        
        /// <summary>
        /// Integer representation of the parsed value
        /// </summary>
        /// <exception cref="ArgumentException">Data type is not convertible</exception>
        [JsonIgnore]
        public int AsInt
        {
            get
            {
                if (ParsedValue is int || ParsedValue is float)
                {
                    return Convert.ToInt32(ParsedValue);
                }
                
                throw new ArgumentException($"Cannot convert {Letter} parameter to integer (value {AsString})");
            }
        }

        /// <summary>
        /// Unsigned integer representation of the parsed value
        /// </summary>
        /// <exception cref="ArgumentException">Data type is not convertible</exception>
        [JsonIgnore]
        public uint AsUInt
        {
            get
            {
                if (ParsedValue is int || ParsedValue is long)
                {
                    return Convert.ToUInt32(ParsedValue);
                }
                
                throw new ArgumentException($"Cannot convert {Letter} parameter to long (value {AsString})");
            }
        }
        
        /// <summary>
        /// Boolean representation of the parsed value
        /// </summary>
        [JsonIgnore]
        public bool AsBool
        {
            get => AsInt > 0;
        }

        /// <summary>
        /// Float array representation of the parsed value
        /// </summary>
        /// <exception cref="ArgumentException">Data type is not convertible</exception>
        [JsonIgnore]
        public float[] AsFloatArray
        {
            get
            {
                if (ParsedValue is float[])
                {
                    return (float[])ParsedValue;
                }

                if (ParsedValue is int[])
                {
                    return ((int[])ParsedValue).Select(Convert.ToSingle).ToArray();
                }
                
                if (ParsedValue is uint[])
                {
                    return ((uint[])ParsedValue).Select(Convert.ToSingle).ToArray();
                }
                
                throw new ArgumentException($"Cannot convert {Letter} parameter to float array (value {AsString})");
            }
        }

        /// <summary>
        /// Integer array representation of the parsed value
        /// </summary>
        /// <exception cref="ArgumentException">Data type is not convertible</exception>
        [JsonIgnore]
        public int[] AsIntArray
        {
            get
            {
                if (ParsedValue is int[])
                {
                    return (int[])ParsedValue;
                }

                if (ParsedValue is float[])
                {
                    return ((float[])ParsedValue).Select(Convert.ToInt32).ToArray();
                }
                
                throw new ArgumentException($"Cannot convert {Letter} parameter to integer array (value {AsString})");
            }
        }

        /// <summary>
        /// Unsigned integer array representation of the parsed value
        /// </summary>
        /// <exception cref="ArgumentException">Data type is not convertible</exception>
        [JsonIgnore]
        public uint[] AsUIntArray
        {
            get
            {
                if (ParsedValue is uint[])
                {
                    return (uint[])ParsedValue;
                }

                if (ParsedValue is float[])
                {
                    return ((float[])ParsedValue).Select(Convert.ToUInt32).ToArray();
                }
                
                throw new ArgumentException($"Cannot convert {Letter} parameter to unsigned integer array (value {AsString})");
            }
        }

        /// <summary>
        /// Data type of the internally parsed value
        /// </summary>
        [JsonIgnore]
        public Type Type => ParsedValue.GetType();
    }
    
    /// <summary>
    /// Converts a <see cref="CodeParameter"/> instance to JSON
    /// </summary>
    public class CodeParameterConverter : JsonConverter
    {
        private class ParameterRepresentation
        {
            public char Letter { get; set; }
            public string Value { get; set; }
            public bool IsString { get; set; }
        }
        
        /// <summary>
        /// Writes a code parameter to JSON
        /// </summary>
        /// <param name="writer">JSON writer</param>
        /// <param name="serializer">JSON Serializer</param>
        /// <param name="value">Value to write</param>
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            CodeParameter parameter = (CodeParameter)value;
            JObject.FromObject(new ParameterRepresentation
            {
                Letter = parameter.Letter,
                Value = parameter.AsString,
                IsString = parameter.Type == typeof(string)
            }).WriteTo(writer);
        }

        /// <summary>
        /// Reads a code parameters from JSON
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="objectType">Object type</param>
        /// <param name="existingValue">Existing value</param>
        /// <param name="serializer">JSON serializer</param>
        /// <returns>Deserialized CodeParameter</returns>
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            ParameterRepresentation representation = JToken.ReadFrom(reader).ToObject<ParameterRepresentation>();
            return new CodeParameter(representation.Letter, representation.Value, representation.IsString);
        }

        /// <summary>
        /// Checks if the corresponding type can be converted
        /// </summary>
        /// <param name="objectType">Object type to check</param>
        /// <returns>Whether the object can be converted</returns>
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(CodeParameter);
        }
    }
}
