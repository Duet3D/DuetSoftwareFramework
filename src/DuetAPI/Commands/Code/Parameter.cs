using DuetAPI.Utility;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        /// <param name="isString">Whether this is a string. This is set to true if the parameter was inside quotation marks</param>
        /// <param name="isDriverId">Whether this is a driver ID</param>
        /// <remarks>This constructor does not parsed long (aka int64) values because RRF cannot handle them</remarks>
        public CodeParameter(char letter, string value, bool isString, bool isDriverId)
        {
            Letter = letter;
            _stringValue = value;

            if (isString)
            {
                // Value is definitely a string because it is encapsulated in quotation marks
                _parsedValue = value;
            }
            else if (isDriverId)
            {
                // Value is a (list of) driver identifier(s)
                List<DriverId> driverIds = new List<DriverId>();

                foreach (string item in value.Split(':'))
                {
                    DriverId driverId = new DriverId(item);
                    driverIds.Add(driverId);
                }

                if (driverIds.Count == 1)
                {
                    _parsedValue = driverIds[0];
                }
                else
                {
                    _parsedValue = driverIds.ToArray();
                }
            }
            else
            {
                // It is not encapsulated...
                value = value.Trim();

                if (string.IsNullOrEmpty(value))
                {
                    // Empty parameters are repesented as integers with the value 0 (e.g. G92 XY => G92 X0 Y0)
                    _parsedValue = 0;
                }
                else if (value.StartsWith('{') && value.EndsWith('}'))
                {
                    // It is an expression
                    IsExpression = true;
                    _parsedValue = value;
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
        /// <param name="letter">Letter of the code parameter (automatically converted to upper-case)</param>
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
        /// <param name="code">Code that owns this parameter</param>
        /// <exception cref="CodeParserException">Driver ID could not be parsed</exception>
        public void ConvertDriverIds(Code code)
        {
            if (!IsExpression)
            {
                List<DriverId> drivers = new List<DriverId>();

                string[] parameters = _stringValue.Split(':');
                foreach (string value in parameters)
                {
                    try
                    {
                        DriverId id = new DriverId(value);
                        drivers.Add(id);
                    }
                    catch (ArgumentException e)
                    {
                        throw new CodeParserException(e.Message + $" from {Letter} parameter", code);
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
            if (codeParameter is null)
            {
                throw new ArgumentNullException(nameof(codeParameter));
            }
            if (codeParameter._parsedValue is float floatValue)
            {
                return floatValue;
            }
            if (codeParameter._parsedValue is int intValue)
            {
                return Convert.ToSingle(intValue);
            }
            if (codeParameter._parsedValue is uint uintValue)
            {
                return Convert.ToSingle(uintValue);
            }
            // long won't fit
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
            if (codeParameter is null)
            {
                throw new ArgumentNullException(nameof(codeParameter));
            }
            if (codeParameter._parsedValue is int intValue)
            {
                return intValue;
            }
            if (codeParameter._parsedValue is float floatValue)
            {
                return Convert.ToInt32(floatValue);
            }
            // long and uint won't fit
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
            if (codeParameter is null)
            {
                throw new ArgumentNullException(nameof(codeParameter));
            }
            if (codeParameter._parsedValue is uint uintValue)
            {
                return uintValue;
            }
            if (codeParameter._parsedValue is int intValue)
            {
                return Convert.ToUInt32(intValue);
            }
            if (codeParameter._parsedValue is float floatValue)
            {
                return Convert.ToUInt32(floatValue);
            }
            // long won't fit
            throw new ArgumentException($"Cannot convert {codeParameter.Letter} parameter to unsigned integer (value {codeParameter._stringValue})");
        }

        /// <summary>
        /// Implicit conversion operator to a driver ID
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="ArgumentException">Data type is not convertible</exception>
        public static implicit operator DriverId(CodeParameter codeParameter)
        {
            if (codeParameter is null)
            {
                throw new ArgumentNullException(nameof(codeParameter));
            }
            if (codeParameter._parsedValue is DriverId driverId)
            {
                return driverId;
            }
            if (codeParameter._parsedValue is uint uintValue)
            {
                return new DriverId(uintValue);
            }
            throw new ArgumentException($"Cannot convert {codeParameter.Letter} parameter to driver ID (value {codeParameter._stringValue})");
        }

        /// <summary>
        /// Implicit conversion operator to long
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="ArgumentException">Data type is not convertible</exception>
        public static implicit operator long(CodeParameter codeParameter)
        {
            if (codeParameter is null)
            {
                throw new ArgumentNullException(nameof(codeParameter));
            }
            if (codeParameter._parsedValue is long longValue)
            {
                return longValue;
            }
            if (codeParameter._parsedValue is int intValue)
            {
                return Convert.ToInt64(intValue);
            }
            if (codeParameter._parsedValue is uint uintValue)
            {
                return Convert.ToInt64(uintValue);
            }
            if (codeParameter._parsedValue is float floatValue)
            {
                return Convert.ToInt64(floatValue);
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
        public static implicit operator string(CodeParameter codeParameter) => codeParameter?._stringValue;

        /// <summary>
        /// Implicit conversion operator to float array
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="ArgumentException">Data type is not convertible</exception>
        public static implicit operator float[](CodeParameter codeParameter)
        {
            if (codeParameter is null)
            {
                return null;
            }
            if (codeParameter._parsedValue is float[] floatArray)
            {
                return floatArray;
            }
            if (codeParameter._parsedValue is float floatValue)
            {
                return new float[] { floatValue };
            }
            if (codeParameter._parsedValue is int intValue)
            {
                return new float[] { Convert.ToSingle(intValue) };
            }
            if (codeParameter._parsedValue is uint uintValue)
            {
                return new float[] { Convert.ToSingle(uintValue) };
            }
            if (codeParameter._parsedValue is int[] intArray)
            {
                return intArray.Select(Convert.ToSingle).ToArray();
            }
            if (codeParameter._parsedValue is uint[] uintArray)
            {
                return uintArray.Select(Convert.ToSingle).ToArray();
            }
            // long won't fit
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
            if (codeParameter is null)
            {
                return null;
            }
            if (codeParameter._parsedValue is int[] intArray)
            {
                return intArray;
            }
            if (codeParameter._parsedValue is int intValue)
            {
                return new int[] { intValue };
            }
            if (codeParameter._parsedValue is float floatValue)
            {
                return new int[] { Convert.ToInt32(floatValue) };
            }
            if (codeParameter._parsedValue is float[] floatArray)
            {
                return floatArray.Select(Convert.ToInt32).ToArray();
            }
            // uint and long won't fit
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
            if (codeParameter is null)
            {
                return null;
            }
            if (codeParameter._parsedValue is uint[] uintArray)
            {
                return uintArray;
            }
            if (codeParameter._parsedValue is uint uintValue)
            {
                return new uint[] { uintValue };
            }
            if (codeParameter._parsedValue is int intValue && intValue >= 0)
            {
                return new uint[] { Convert.ToUInt32(intValue) };
            }
            if (codeParameter._parsedValue is int[] intArray && intArray.All(value => value >= 0))
            {
                return intArray.Select(Convert.ToUInt32).ToArray();
            }
            if (codeParameter._parsedValue is float floatValue && floatValue >= 0F)
            {
                return new uint[] { Convert.ToUInt32(floatValue) };
            }
            if (codeParameter._parsedValue is float[] floatArray && floatArray.All(value => value >= 0F))
            {
                return floatArray.Select(Convert.ToUInt32).ToArray();
            }
            // long won't fit
            throw new ArgumentException($"Cannot convert {codeParameter.Letter} parameter to unsigned integer array (value {codeParameter._stringValue})");
        }

        /// <summary>
        /// Implicit conversion operator to a driver ID array
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="ArgumentException">Data type is not convertible</exception>
        public static implicit operator DriverId[](CodeParameter codeParameter)
        {
            if (codeParameter is null)
            {
                throw new ArgumentNullException(nameof(codeParameter));
            }
            if (codeParameter._parsedValue is DriverId[] driverIdArray)
            {
                return driverIdArray;
            }
            if (codeParameter._parsedValue is DriverId driverId)
            {
                return new DriverId[] { driverId };
            }
            throw new ArgumentException($"Cannot convert {codeParameter.Letter} parameter to driver ID array (value {codeParameter._stringValue})");
        }

        /// <summary>
        /// Implicit conversion operator to long array
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="ArgumentException">Data type is not convertible</exception>
        public static implicit operator long[] (CodeParameter codeParameter)
        {
            if (codeParameter is null)
            {
                return null;
            }
            if (codeParameter._parsedValue is long[] longArray)
            {
                return longArray;
            }
            if (codeParameter._parsedValue is long longValue)
            {
                return new long[] { longValue };
            }
            if (codeParameter._parsedValue is int intValue)
            {
                return new long[] { Convert.ToInt64(intValue) };
            }
            if (codeParameter._parsedValue is uint uintValue)
            {
                return new long[] { Convert.ToInt64(uintValue) };
            }
            if (codeParameter._parsedValue is float floatValue)
            {
                return new long[] { Convert.ToInt64(floatValue) };
            }
            if (codeParameter._parsedValue is int[] intArray)
            {
                return intArray.Select(Convert.ToInt64).ToArray();
            }
            if (codeParameter._parsedValue is int[] uintArray)
            {
                return uintArray.Select(Convert.ToInt64).ToArray();
            }
            if (codeParameter._parsedValue is float[] floatArray)
            {
                return floatArray.Select(Convert.ToInt64).ToArray();
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
    public class CodeParameterConverter : JsonConverter<CodeParameter>
    {
        /// <summary>
        /// Read a CodeParameter object from JSON
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="typeToConvert">Type to convert</param>
        /// <param name="options">Serializer options</param>
        /// <returns>Read value</returns>
        public override CodeParameter Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                char letter = '\0';
                string propertyName = string.Empty, value = string.Empty;
                bool isString = false, isDriverId = false;

                while (reader.Read())
                {
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.PropertyName:
                            propertyName = reader.GetString();
                            break;

                        case JsonTokenType.String:
                            if (propertyName.Equals("letter", StringComparison.InvariantCultureIgnoreCase))
                            {
                                letter = Convert.ToChar(reader.GetString());
                            }
                            else if (propertyName.Equals("value", StringComparison.InvariantCultureIgnoreCase))
                            {
                                value = reader.GetString();
                            }
                            break;

                        // null parameter values are not supported

                        case JsonTokenType.Number:
                            if (propertyName.Equals("isString", StringComparison.InvariantCultureIgnoreCase))
                            {
                                isString = Convert.ToBoolean(reader.GetInt32());
                            }
                            else if (propertyName.Equals("isDriverId", StringComparison.InvariantCultureIgnoreCase))
                            {
                                isDriverId = Convert.ToBoolean(reader.GetInt32());
                            }
                            break;

                        case JsonTokenType.True:
                        case JsonTokenType.False:
                            if (propertyName.Equals("isString", StringComparison.InvariantCultureIgnoreCase))
                            {
                                isString = (reader.TokenType == JsonTokenType.True);
                            }
                            else if (propertyName.Equals("isDriverId", StringComparison.InvariantCultureIgnoreCase))
                            {
                                isDriverId = (reader.TokenType == JsonTokenType.True);
                            }
                            break;

                        case JsonTokenType.EndObject:
                            return new CodeParameter(letter, value, isString, isDriverId);
                    }
                }
            }
            throw new JsonException("Invalid code parameter");
        }

        /// <summary>
        /// Write a CodeParameter to JSON
        /// </summary>
        /// <param name="writer">JSON writer</param>
        /// <param name="value">Value to serialize</param>
        /// <param name="options">Write options</param>
        public override void Write(Utf8JsonWriter writer, CodeParameter value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("letter", value.Letter.ToString());
            writer.WriteString("value", value);
            if (value.Type == typeof(DriverId) || value.Type == typeof(DriverId[]))
            {
                writer.WriteBoolean("isDriverId", true);
            }
            writer.WriteBoolean("isString", value.Type == typeof(string));
            writer.WriteEndObject();
        }
    }
}
