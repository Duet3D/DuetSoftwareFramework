using System;
using Newtonsoft.Json;
using System.Globalization;
using System.Linq;
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
        /// Internal parsed representation of the string value (one of string, int, long, double, int[], long[] or double[])
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
                            // If there is a dot anywhere, attempt to parse it as a double array
                            ParsedValue = subArgs.Select(subArg => double.Parse(subArg, NumberStyles.Any, CultureInfo.InvariantCulture)).ToArray();
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
                                // If that failed, attempt to parse everything as a long array
                                ParsedValue = subArgs.Select(long.Parse);
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
                else if (long.TryParse(value, out long asLong))
                {
                    // It is a valid long
                    ParsedValue = asLong;
                }
                else if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double asDouble))
                {
                    // It is a valid double
                    ParsedValue = asDouble;
                }
                else
                {
                    // It must be a string (fallback)
                    ParsedValue = value;
                }
            }
        }

        /// <summary>
        /// Integer representation of the parsed value
        /// </summary>
        /// <exception cref="ArgumentException">Thrown if the data type is not convertible</exception>
        [JsonIgnore]
        public int AsInt
        {
            get
            {
                if (ParsedValue is int || ParsedValue is double)
                {
                    return Convert.ToInt32(ParsedValue);
                }
                
                throw new ArgumentException($"Cannot convert {Letter} parameter to integer (value {AsString})");
            }
        }

        /// <summary>
        /// Long representation of the parsed value
        /// </summary>
        /// <exception cref="ArgumentException">Thrown if the data type is not convertible</exception>
        [JsonIgnore]
        public long AsLong
        {
            get
            {
                if (ParsedValue is int || ParsedValue is long)
                {
                    return Convert.ToInt64(ParsedValue);
                }
                
                throw new ArgumentException($"Cannot convert {Letter} parameter to long (value {AsString})");
            }
        }
        
        /// <summary>
        /// Double representation of the parsed value
        /// </summary>
        /// <exception cref="ArgumentException">Thrown if the data type is not convertible</exception>
        [JsonIgnore]
        public double AsDouble
        {
            get
            {
                if (ParsedValue is int || ParsedValue is double)
                {
                    return Convert.ToDouble(ParsedValue);
                }
                
                throw new ArgumentException($"Cannot convert {Letter} parameter to double (value {AsString})");
            }
        }
        
        
        /// <summary>
        /// Integer array representation of the parsed value
        /// </summary>
        /// <exception cref="ArgumentException">Thrown if the data type is not convertible</exception>
        [JsonIgnore]
        public int[] AsIntArray
        {
            get
            {
                if (ParsedValue is int[])
                {
                    return (int[])ParsedValue;
                }

                if (ParsedValue is double[])
                {
                    return ((double[])ParsedValue).Select(Convert.ToInt32).ToArray();
                }
                
                throw new ArgumentException($"Cannot convert {Letter} parameter to integer array (value {AsString})");
            }
        }

        /// <summary>
        /// Long array representation of the parsed value
        /// </summary>
        /// <exception cref="ArgumentException">Thrown if the data type is not convertible</exception>
        [JsonIgnore]
        public long[] AsLongArray
        {
            get
            {
                if (ParsedValue is long[])
                {
                    return (long[])ParsedValue;
                }
                
                if (ParsedValue is int[])
                {
                    return ((int[])ParsedValue).Select(Convert.ToInt64).ToArray();
                }

                throw new ArgumentException($"Cannot convert {Letter} parameter to long array (value {AsString})");
            }
        }

        /// <summary>
        /// Double array representation of the parsed value
        /// </summary>
        /// <exception cref="ArgumentException"></exception>
        [JsonIgnore]
        public double[] AsDoubleArray
        {
            get
            {
                if (ParsedValue is double[])
                {
                    return (double[])ParsedValue;
                }

                if (ParsedValue is int[])
                {
                    return ((int[])ParsedValue).Select(Convert.ToDouble).ToArray();
                }
                
                if (ParsedValue is long[])
                {
                    return ((long[])ParsedValue).Select(Convert.ToDouble).ToArray();
                }
                
                throw new ArgumentException($"Cannot convert {Letter} parameter to double array (value {AsString})");
            }
        }

        /// <summary>
        /// Data type of the internally parsed value
        /// </summary>
        [JsonIgnore]
        public Type Type
        {
            get => ParsedValue.GetType();
        }
    }
    
    /// <summary>
    /// Converts a <see cref="CodeParameter"/> instance to JSON
    /// </summary>
    public class CodeParameterConverter : JsonConverter
    {
        class ParameterRepresentation
        {
            public char Letter { get; set; }
            public string Value { get; set; }
            public bool IsString { get; set; }
        }
        
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

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            ParameterRepresentation representation = JToken.ReadFrom(reader).ToObject<ParameterRepresentation>();
            return new CodeParameter(representation.Letter, representation.Value, representation.IsString);
        }

        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(CodeParameter);
        }
    }
}
