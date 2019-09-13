using System;
using System.Collections.Generic;
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
        /// Indicates if this parameter is an expression that can be evaluated
        /// </summary>
        public bool IsExpression { get; }

        /// <summary>
        /// Indicates if this parameter is a driver identifier
        /// </summary>
        public bool IsDriverId { get; private set; }

        /// <summary>
        /// Unparsed string representation of the code parameter or an empty string if none present
        /// </summary>
        private readonly string _stringValue;

        /// <summary>
        /// Internal parsed representation of the string value (one of string, int, uint, float, int[], uint[] or float[])
        /// </summary>
        private object _parsedValue;
        
        /// <summary>
        /// Creates a new CodeParameter instance and parses value to a native data type if applicable
        /// </summary>
        /// <param name="letter">Letter of the code parameter</param>
        /// <param name="value">String representation of the value</param>
        /// <param name="isString">Whether this is a string. This is set to true if the parameter was inside quotation marks.</param>
        /// <remarks>This constructor does not parsed long (aka int64) values because RRF cannot handle them</remarks>
        public CodeParameter(char letter, string value, bool isString)
        {
            Letter = letter;
            _stringValue = value;

            if (isString)
            {
                // Value is definitely a string because it is encapsulated in quotation marks
                _parsedValue = value;
            }
            else
            {
                // It is not encapsulated...
                value = value.Trim();

                if (value == "")
                {
                    // Empty parameters are repesented as integers with the value 0 (e.g. G92 XY => G92 X0 Y0)
                    _parsedValue = 0;
                }
                else if (value.StartsWith('{') && value.EndsWith('}'))
                {
                    // It is an expression
                    IsExpression = true;
                }
                else if (value.Contains(':'))
                {
                    // It is an array (or a string)
                    string[] subArgs = value.Split(':');
                    try
                    {
                        if (value.Contains('.'))
                        {
                            // If there is a dot anywhere, attempt to parse it as a float array
                            _parsedValue = subArgs.Select(subArg => float.Parse(subArg, NumberStyles.Any, CultureInfo.InvariantCulture)).ToArray();
                        }
                        else
                        {
                            try
                            {
                                // If there is no dot, it could be an integer array
                                _parsedValue = subArgs.Select(int.Parse).ToArray();
                            }
                            catch
                            {
                                // If that failed, attempt to parse everything as a uint array
                                _parsedValue = subArgs.Select(uint.Parse).ToArray();
                            }
                        }
                    }
                    catch
                    {
                        // It must be a string (fallback)
                        _parsedValue = value;
                    }
                }
                else if (int.TryParse(value, out int asInt))
                {
                    // It is a valid integer
                    _parsedValue = asInt;
                }
                else if (uint.TryParse(value, out uint asUInt))
                {
                    // It is a valid unsigned integer
                    _parsedValue = asUInt;
                }
                else if (float.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out float asFloat))
                {
                    // It is a valid float
                    _parsedValue = asFloat;
                }
                else
                {
                    // It is a string
                    _parsedValue = value;
                }
            }
        }

        /// <summary>
        /// Creates a new CodeParameter instance and with the given value
        /// </summary>
        /// <param name="letter">Letter of the code parameter</param>
        /// <param name="value">Value of this parameter</param>
        public CodeParameter(char letter, object value)
        {
            Letter = letter;
            _stringValue = value.ToString();
            _parsedValue = value;
        }

        /// <summary>
        /// Convert this parameter to driver id(s)
        /// </summary>
        /// <remarks>The data remains a uint (array) after the conversion. The top 16 bits reflect the board number and the bottom 16 bits the port</remarks>
        public void ConvertDriverIds()
        {
            if (!IsExpression)
            {
                List<uint> drivers = new List<uint>();

                string[] parameters = _stringValue.Split(':');
                foreach (string value in parameters)
                {
                    string[] segments = value.Split('.');
                    if (segments.Length == 1)
                    {
                        if (ushort.TryParse(segments[0], out ushort port))
                        {
                            drivers.Add(port);
                        }
                        else
                        {
                            throw new CodeParserException($"Failed to parse driver number from {Letter} parameter");
                        }
                    }
                    else if (segments.Length == 2)
                    {
                        uint driver;
                        if (ushort.TryParse(segments[0], out ushort board))
                        {
                            driver = (uint)board << 16;
                        }
                        else
                        {
                            throw new CodeParserException($"Failed to parse board number from {Letter} parameter");
                        }
                        if (ushort.TryParse(segments[1], out ushort port))
                        {
                            driver |= port;
                        }
                        else
                        {
                            throw new CodeParserException($"Failed to parse driver number from {Letter} parameter");
                        }
                        drivers.Add(driver);
                    }
                    else
                    {
                        throw new CodeParserException($"Driver value from {Letter} parameter is invalid");
                    }
                }

                if (drivers.Count == 1)
                {
                    _parsedValue = drivers[0];
                }
                else
                {
                    _parsedValue = drivers.ToArray();
                }
                IsDriverId = true;
            }
        }

        /// <summary>
        /// Data type of the internally parsed value
        /// </summary>
        [JsonIgnore]
        public Type Type => _parsedValue.GetType();

        /// <summary>
        /// Implicit conversion operator to float
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="ArgumentException">Data type is not convertible</exception>
        public static implicit operator float(CodeParameter codeParameter)
        {
            if (codeParameter._parsedValue is int || codeParameter._parsedValue is uint || codeParameter._parsedValue is float)
            {
                return Convert.ToSingle(codeParameter._parsedValue);
            }
            throw new ArgumentException($"Cannot convert {codeParameter.Letter} parameter to float (value {codeParameter._stringValue})");
        }
        
        /// <summary>
        /// Implicit conversion operator to int
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="ArgumentException">Data type is not convertible</exception>
        public static implicit operator int(CodeParameter codeParameter)
        {
            if (codeParameter._parsedValue is int || codeParameter._parsedValue is float)
            {
                return Convert.ToInt32(codeParameter._parsedValue);
            }

            throw new ArgumentException($"Cannot convert {codeParameter.Letter} parameter to integer (value {codeParameter._stringValue})");
        }

        /// <summary>
        /// Implicit conversion operator to uint
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="ArgumentException">Data type is not convertible</exception>
        public static implicit operator uint(CodeParameter codeParameter)
        {
            if (codeParameter._parsedValue is uint || codeParameter._parsedValue is int || codeParameter._parsedValue is float)
            {
                return Convert.ToUInt32(codeParameter._parsedValue);
            }
            throw new ArgumentException($"Cannot convert {codeParameter.Letter} parameter to unsigned integer (value {codeParameter._stringValue})");
        }

        /// <summary>
        /// Implicit conversion operator to long
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="ArgumentException">Data type is not convertible</exception>
        public static implicit operator long(CodeParameter codeParameter)
        {
            if (codeParameter._parsedValue is uint || codeParameter._parsedValue is int || codeParameter._parsedValue is float)
            {
                return Convert.ToInt64(codeParameter._parsedValue);
            }
            throw new ArgumentException($"Cannot convert {codeParameter.Letter} parameter to long (value {codeParameter._stringValue})");
        }

        /// <summary>
        /// Implicit conversion operator to bool
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="ArgumentException">Data type is not convertible</exception>
        public static implicit operator bool(CodeParameter codeParameter) => codeParameter > 0;

        /// <summary>
        /// Implicit conversion operator to string
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        public static implicit operator string(CodeParameter codeParameter) => codeParameter._stringValue;

        /// <summary>
        /// Implicit conversion operator to float array
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="ArgumentException">Data type is not convertible</exception>
        public static implicit operator float[](CodeParameter codeParameter)
        {
            if (codeParameter._parsedValue is float[])
            {
                return (float[])codeParameter._parsedValue;
            }
            if (codeParameter._parsedValue is int intValue)
            {
                return new float[] { intValue };
            }
            if (codeParameter._parsedValue is uint uintValue)
            {
                return new float[] { uintValue };
            }
            if (codeParameter._parsedValue is int[])
            {
                return ((int[])codeParameter._parsedValue).Select(Convert.ToSingle).ToArray();
            }
            if (codeParameter._parsedValue is uint[])
            {
                return ((uint[])codeParameter._parsedValue).Select(Convert.ToSingle).ToArray();
            }
            throw new ArgumentException($"Cannot convert {codeParameter.Letter} parameter to float array (value {codeParameter._stringValue})");
        }

        /// <summary>
        /// Implicit conversion operator to integer array
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="ArgumentException">Data type is not convertible</exception>
        public static implicit operator int[] (CodeParameter codeParameter)
        {
            if (codeParameter._parsedValue is int[])
            {
                return (int[])codeParameter._parsedValue;
            }
            if (codeParameter._parsedValue is int intValue)
            {
                return new int[] { intValue };
            }
            if (codeParameter._parsedValue is float[])
            {
                return ((float[])codeParameter._parsedValue).Select(Convert.ToInt32).ToArray();
            }
            throw new ArgumentException($"Cannot convert {codeParameter.Letter} parameter to integer array (value {codeParameter._stringValue})");
        }

        /// <summary>
        /// Implicit conversion operator to unsigned integer array
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="ArgumentException">Data type is not convertible</exception>
        public static implicit operator uint[] (CodeParameter codeParameter)
        {
            if (codeParameter._parsedValue is uint[])
            {
                return (uint[])codeParameter._parsedValue;
            }
            if (codeParameter._parsedValue is int intValue && intValue >= 0)
            {
                return new uint[] { (uint)intValue };
            }
            if (codeParameter._parsedValue is uint uintValue)
            {
                return new uint[] { uintValue };
            }
            if (codeParameter._parsedValue is float[])
            {
                return ((float[])codeParameter._parsedValue).Select(Convert.ToUInt32).ToArray();
            }
            throw new ArgumentException($"Cannot convert {codeParameter.Letter} parameter to unsigned integer array (value {codeParameter._stringValue})");
        }

        /// <summary>
        /// Implicit conversion operator to long array
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="ArgumentException">Data type is not convertible</exception>
        public static implicit operator long[] (CodeParameter codeParameter)
        {
            if (codeParameter._parsedValue is long[])
            {
                return (long[])codeParameter._parsedValue;
            }
            if (codeParameter._parsedValue is int intValue)
            {
                return new long[] { intValue };
            }
            if (codeParameter._parsedValue is uint uintValue)
            {
                return new long[] { uintValue };
            }
            if (codeParameter._parsedValue is long longValue)
            {
                return new long[] { longValue };
            }
            if (codeParameter._parsedValue is int[] || codeParameter._parsedValue is uint[] || codeParameter._parsedValue is float[])
            {
                return ((long[])codeParameter._parsedValue).Select(Convert.ToInt64).ToArray();
            }
            throw new ArgumentException($"Cannot convert {codeParameter.Letter} parameter to long array (value {codeParameter._stringValue})");
        }

        /// <summary>
        /// Equality operator
        /// </summary>
        /// <param name="a">Code parameter</param>
        /// <param name="b">Other object</param>
        /// <returns>True if both objects are equal</returns>
        public static bool operator ==(CodeParameter a, object b)
        {
            if (a is null)
            {
                return (b is null);
            }
            if (b is CodeParameter other)
            {
                return a.Letter.Equals(other.Letter) && a._parsedValue.Equals(other._parsedValue);
            }
            return a._parsedValue.Equals(b);
        }

        /// <summary>
        /// Inequality operator
        /// </summary>
        /// <param name="a">Code parameter</param>
        /// <param name="b">Other object</param>
        /// <returns>True if both objects are not equal</returns>
        public static bool operator !=(CodeParameter a, object b) => !(a == b);

        /// <summary>
        /// Checks if the other obj equals this instance
        /// </summary>
        /// <param name="obj">Other object</param>
        /// <returns>True if both objects are not equal</returns>
        public override bool Equals(object obj) => this == obj;

        /// <summary>
        /// Returns the hash code of this instance
        /// </summary>
        /// <returns>Computed hash code</returns>
        public override int GetHashCode()
        {
            return HashCode.Combine(Letter, _parsedValue);
        }

        /// <summary>
        /// Converts this parameter to a string
        /// </summary>
        /// <returns>String representation</returns>
        public override string ToString() => Letter + _stringValue;
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
                Value = parameter,
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
