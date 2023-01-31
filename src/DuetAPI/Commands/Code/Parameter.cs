using DuetAPI.Utility;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Net;
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
        /// <remarks>
        /// If this is an unprecedented parameter without a letter, '@' is used
        /// </remarks>
        public char Letter { get; }

        /// <summary>
        /// Indicates if this parameter is an expression that can be evaluated
        /// </summary>
        public bool IsExpression { get; }

        /// <summary>
        /// Indicates if this parameter is a driver identifier
        /// </summary>
        public bool IsDriverId { get; internal set; }

        /// <summary>
        /// Unparsed string representation of the code parameter or an empty string if none present
        /// </summary>
        internal readonly string? StringValue;

        /// <summary>
        /// Internal parsed representation of the string value (one of string, int, uint, float, int[], uint[] or float[])
        /// </summary>
        internal object? ParsedValue;

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
            StringValue = value;

            if (isString)
            {
                // Value definitely a string because it is encapsulated in quotation marks
                ParsedValue = value;
            }
            else if (string.IsNullOrEmpty(value))
            {
                // No value present (e.g. G28 X)
                ParsedValue = null;
            }
            else if (isDriverId)
            {
                // Value is a (list of) driver identifier(s)
                List<DriverId> driverIds = new();

                foreach (string item in value.Split(':'))
                {
                    DriverId driverId = new(item);
                    driverIds.Add(driverId);
                }

                if (driverIds.Count == 1)
                {
                    ParsedValue = driverIds[0];
                }
                else
                {
                    ParsedValue = driverIds.ToArray();
                }
            }
            else
            {
                // It is not encapsulated...
                value = value.Trim();

                if (value.StartsWith("{") && value.EndsWith("}"))
                {
                    // It is an expression
                    IsExpression = true;
                    ParsedValue = value;
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
                            ParsedValue = subArgs.Select(subArg => float.Parse(subArg, NumberStyles.Any, CultureInfo.InvariantCulture)).ToArray();
                        }
                        else
                        {
                            try
                            {
                                // If there is no dot, it could be an integer array
                                ParsedValue = subArgs.Select(int.Parse).ToArray();
                            }
                            catch
                            {
                                // If that failed, attempt to parse everything as a uint array
                                ParsedValue = subArgs.Select(uint.Parse).ToArray();
                            }
                        }
                    }
                    catch
                    {
                        // It must be a string (fallback)
                        ParsedValue = value;
                    }
                }
                else if (value.StartsWith("0x") && int.TryParse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int asHexInt))
                {
                    // It is a hex integer
                    ParsedValue = asHexInt;
                }
                else if (int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out int asInt))
                {
                    // It is a valid integer
                    ParsedValue = asInt;
                }
                else if (value.StartsWith("0x") && uint.TryParse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint asHexUInt))
                {
                    // It is a hex unsigned integer
                    ParsedValue = asHexUInt;
                }
                else if (uint.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out uint asUInt))
                {
                    // It is a valid unsigned integer
                    ParsedValue = asUInt;
                }
                else if (float.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out float asFloat))
                {
                    // It is a valid float
                    ParsedValue = asFloat;
                }
                else
                {
                    // It is a string
                    ParsedValue = value;
                }
            }
        }

        /// <summary>
        /// Creates a new CodeParameter instance and with the given value
        /// </summary>
        /// <param name="letter">Letter of the code parameter (automatically converted to upper-case)</param>
        /// <param name="value">Value of this parameter</param>
        public CodeParameter(char letter, object? value)
        {
            Letter = letter;
            ParsedValue = value;
            if (value is string stringValue)
            {
                StringValue = stringValue;
                if (stringValue.StartsWith("{") && stringValue.EndsWith("}"))
                {
                    IsExpression = true;
                }
            }
            else if (value is int intValue)
            {
                StringValue = intValue.ToString("G", CultureInfo.InvariantCulture);
            }
            else if (value is uint uintValue)
            {
                StringValue = uintValue.ToString("G", CultureInfo.InvariantCulture);
            }
            else if (value is float floatValue)
            {
                StringValue = floatValue.ToString("G", CultureInfo.InvariantCulture);
            }
            else if (value is long longValue)
            {
                StringValue = longValue.ToString("G", CultureInfo.InvariantCulture);
            }
            else if (value is int[] intArray)
            {
                StringValue = string.Join(":", intArray.Select(intVal => intVal.ToString("G", CultureInfo.InvariantCulture)));
            }
            else if (value is uint[] uintArray)
            {
                StringValue = string.Join(":", uintArray.Select(uintVal => uintVal.ToString("G", CultureInfo.InvariantCulture)));
            }
            else if (value is float[] floatArray)
            {
                StringValue = string.Join(":", floatArray.Select(floatVal => floatVal.ToString("G", CultureInfo.InvariantCulture)));
            }
            else if (value is long[] longArray)
            {
                StringValue = string.Join(":", longArray.Select(longVal => longVal.ToString("G", CultureInfo.InvariantCulture)));
            }
            else if (value is not null)
            {
                StringValue = value.ToString();
            }
        }

        /// <summary>
        /// Data type of the internally parsed value
        /// </summary>
        [JsonIgnore]
        public Type? Type => ParsedValue?.GetType();

        /// <summary>
        /// Check if the parameter does not have any data
        /// </summary>
        [JsonIgnore]
        public bool IsNull { get => Type is null; }

        #region Explicit cast operators
        /// <summary>
        /// Explicit conversion operator to float
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public static explicit operator float([NotNull] CodeParameter? codeParameter)
        {
            if (codeParameter is not null)
            {
                if (codeParameter.ParsedValue is float floatValue)
                {
                    return floatValue;
                }
                if (codeParameter.ParsedValue is int intValue)
                {
                    return Convert.ToSingle(intValue);
                }
                if (codeParameter.ParsedValue is uint uintValue)
                {
                    return Convert.ToSingle(uintValue);
                }
                // long won't fit
            }
            throw new InvalidParameterTypeException(codeParameter, typeof(float));
        }

        /// <summary>
        /// Explicit conversion operator to float?
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public static explicit operator float?(CodeParameter? codeParameter)
        {
            if (codeParameter is null)
            {
                return null;
            }
            if (codeParameter.ParsedValue is float floatValue)
            {
                return floatValue;
            }
            if (codeParameter.ParsedValue is int intValue)
            {
                return Convert.ToSingle(intValue);
            }
            if (codeParameter.ParsedValue is uint uintValue)
            {
                return Convert.ToSingle(uintValue);
            }
            // long won't fit
            throw new InvalidParameterTypeException(codeParameter, typeof(float?));
        }

        /// <summary>
        /// Explicit conversion operator to int
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public static explicit operator int([NotNull] CodeParameter? codeParameter)
        {
            if (codeParameter is not null)
            {
                if (codeParameter.ParsedValue is int intValue)
                {
                    return intValue;
                }
                if (codeParameter.ParsedValue is float floatValue)
                {
                    return Convert.ToInt32(floatValue);
                }
                // long and uint won't fit
            }
            throw new InvalidParameterTypeException(codeParameter, typeof(int));
        }

        /// <summary>
        /// Explicit conversion operator to int?
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public static explicit operator int?(CodeParameter? codeParameter)
        {
            if (codeParameter is null)
            {
                return null;
            }
            if (codeParameter.ParsedValue is int intValue)
            {
                return intValue;
            }
            if (codeParameter.ParsedValue is float floatValue)
            {
                return Convert.ToInt32(floatValue);
            }
            // long and uint won't fit
            throw new InvalidParameterTypeException(codeParameter, typeof(int?));
        }

        /// <summary>
        /// Explicit conversion operator to uint
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public static explicit operator uint([NotNull] CodeParameter? codeParameter)
        {
            if (codeParameter is not null)
            {
                if (codeParameter.ParsedValue is uint uintValue)
                {
                    return uintValue;
                }
                if (codeParameter.ParsedValue is DriverId driverIdValue)
                {
                    return driverIdValue;
                }
                if (codeParameter.ParsedValue is int intValue)
                {
                    return Convert.ToUInt32(intValue);
                }
                if (codeParameter.ParsedValue is float floatValue)
                {
                    return Convert.ToUInt32(floatValue);
                }
                // long won't fit
            }
            throw new InvalidParameterTypeException(codeParameter, typeof(uint));
        }

        /// <summary>
        /// Explicit conversion operator to uint?
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public static explicit operator uint?(CodeParameter? codeParameter)
        {
            if (codeParameter is null)
            {
                return null;
            }
            if (codeParameter.ParsedValue is uint uintValue)
            {
                return uintValue;
            }
            if (codeParameter.ParsedValue is DriverId driverIdValue)
            {
                return driverIdValue;
            }
            if (codeParameter.ParsedValue is int intValue)
            {
                return Convert.ToUInt32(intValue);
            }
            if (codeParameter.ParsedValue is float floatValue)
            {
                return Convert.ToUInt32(floatValue);
            }
            // long won't fit
            throw new InvalidParameterTypeException(codeParameter, typeof(uint?));
        }

        /// <summary>
        /// Explicit conversion operator to long
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public static explicit operator long([NotNull] CodeParameter? codeParameter)
        {
            if (codeParameter is not null)
            {
                if (codeParameter.ParsedValue is long longValue)
                {
                    return longValue;
                }
                if (codeParameter.ParsedValue is int intValue)
                {
                    return Convert.ToInt64(intValue);
                }
                if (codeParameter.ParsedValue is uint uintValue)
                {
                    return Convert.ToInt64(uintValue);
                }
                if (codeParameter.ParsedValue is float floatValue)
                {
                    return Convert.ToInt64(floatValue);
                }
            }
            throw new InvalidParameterTypeException(codeParameter, typeof(long));
        }

        /// <summary>
        /// Explicit conversion operator to long?
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public static explicit operator long?(CodeParameter? codeParameter)
        {
            if (codeParameter is null)
            {
                return null;
            }
            if (codeParameter.ParsedValue is long longValue)
            {
                return longValue;
            }
            if (codeParameter.ParsedValue is int intValue)
            {
                return Convert.ToInt64(intValue);
            }
            if (codeParameter.ParsedValue is uint uintValue)
            {
                return Convert.ToInt64(uintValue);
            }
            if (codeParameter.ParsedValue is float floatValue)
            {
                return Convert.ToInt64(floatValue);
            }
            throw new InvalidParameterTypeException(codeParameter, typeof(long?));
        }

        /// <summary>
        /// Explicit conversion operator to bool
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public static explicit operator bool([NotNull] CodeParameter? codeParameter) => (int)codeParameter > 0;

        /// <summary>
        /// Explicit conversion operator to bool?
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public static explicit operator bool?(CodeParameter? codeParameter) => (codeParameter != null) ? ((int)codeParameter > 0) : null;

        /// <summary>
        /// Explicit conversion operator to string
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        [return: NotNullIfNotNull(nameof(codeParameter))]
        public static explicit operator string?(CodeParameter? codeParameter)
        {
            if (codeParameter is null)
            {
                return null;
            }
            if (codeParameter.ParsedValue is string stringValue)
            {
                return stringValue;
            }
            throw new InvalidParameterTypeException(codeParameter, typeof(string));
        }

        /// <summary>
        /// Explicit conversion operator to a driver ID
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        [return: NotNullIfNotNull(nameof(codeParameter))]
        public static explicit operator DriverId?(CodeParameter? codeParameter)
        {
            if (codeParameter is null)
            {
                return null;
            }
            if (codeParameter.ParsedValue is DriverId driverId)
            {
                return driverId;
            }
            if (codeParameter.ParsedValue is uint uintValue)
            {
                return new DriverId(uintValue);
            }
            throw new InvalidParameterTypeException(codeParameter, typeof(DriverId));
        }

        /// <summary>
        /// Explicit conversion operator to an IP address
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        [return: NotNullIfNotNull(nameof(codeParameter))]
        public static explicit operator IPAddress?(CodeParameter? codeParameter)
        {
            if (codeParameter is null)
            {
                return null;
            }
            if (codeParameter.ParsedValue is string stringValue)
            {
                return IPAddress.Parse(stringValue);
            }
            throw new InvalidParameterTypeException(codeParameter, typeof(IPAddress));
        }

        /// <summary>
        /// Explicit conversion operator to float array
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        [return: NotNullIfNotNull(nameof(codeParameter))]
        public static explicit operator float[]?(CodeParameter? codeParameter)
        {
            if (codeParameter is null)
            {
                return null;
            }
            if (codeParameter.ParsedValue is float[] floatArray)
            {
                return floatArray;
            }
            if (codeParameter.ParsedValue is float floatValue)
            {
                return new float[] { floatValue };
            }
            if (codeParameter.ParsedValue is int intValue)
            {
                return new float[] { Convert.ToSingle(intValue) };
            }
            if (codeParameter.ParsedValue is uint uintValue)
            {
                return new float[] { Convert.ToSingle(uintValue) };
            }
            if (codeParameter.ParsedValue is int[] intArray)
            {
                return intArray.Select(Convert.ToSingle).ToArray();
            }
            if (codeParameter.ParsedValue is uint[] uintArray)
            {
                return uintArray.Select(Convert.ToSingle).ToArray();
            }
            // long won't fit
            throw new InvalidParameterTypeException(codeParameter, typeof(float[]));
        }

        /// <summary>
        /// Explicit conversion operator to integer array
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        [return: NotNullIfNotNull(nameof(codeParameter))]
        public static explicit operator int[]?(CodeParameter? codeParameter)
        {
            if (codeParameter is null)
            {
                return null;
            }
            if (codeParameter.ParsedValue is int[] intArray)
            {
                return intArray;
            }
            if (codeParameter.ParsedValue is int intValue)
            {
                return new int[] { intValue };
            }
            if (codeParameter.ParsedValue is float floatValue)
            {
                return new int[] { Convert.ToInt32(floatValue) };
            }
            if (codeParameter.ParsedValue is float[] floatArray)
            {
                return floatArray.Select(Convert.ToInt32).ToArray();
            }
            // uint and long won't fit
            throw new InvalidParameterTypeException(codeParameter, typeof(int[]));
        }

        /// <summary>
        /// Explicit conversion operator to unsigned integer array
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        [return: NotNullIfNotNull(nameof(codeParameter))]
        public static explicit operator uint[]?(CodeParameter? codeParameter)
        {
            if (codeParameter is null)
            {
                return null;
            }
            if (codeParameter.ParsedValue is uint[] uintArray)
            {
                return uintArray;
            }
            if (codeParameter.ParsedValue is uint uintValue)
            {
                return new uint[] { uintValue };
            }
            if (codeParameter.ParsedValue is DriverId[] driverIdArray)
            {
                return driverIdArray.Select(value => (uint)value).ToArray();
            }
            if (codeParameter.ParsedValue is DriverId driverIdValue)
            {
                return new uint[] { driverIdValue };
            }
            if (codeParameter.ParsedValue is int intValue && intValue >= 0)
            {
                return new uint[] { Convert.ToUInt32(intValue) };
            }
            if (codeParameter.ParsedValue is int[] intArray && intArray.All(value => value >= 0))
            {
                return intArray.Select(Convert.ToUInt32).ToArray();
            }
            if (codeParameter.ParsedValue is float floatValue && floatValue >= 0F)
            {
                return new uint[] { Convert.ToUInt32(floatValue) };
            }
            if (codeParameter.ParsedValue is float[] floatArray && floatArray.All(value => value >= 0F))
            {
                return floatArray.Select(Convert.ToUInt32).ToArray();
            }
            // long won't fit
            throw new InvalidParameterTypeException(codeParameter, typeof(uint[]));
        }

        /// <summary>
        /// Explicit conversion operator to long array
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        [return: NotNullIfNotNull(nameof(codeParameter))]
        public static explicit operator long[]?(CodeParameter? codeParameter)
        {
            if (codeParameter is null)
            {
                return null;
            }
            if (codeParameter.ParsedValue is long[] longArray)
            {
                return longArray;
            }
            if (codeParameter.ParsedValue is long longValue)
            {
                return new long[] { longValue };
            }
            if (codeParameter.ParsedValue is int intValue)
            {
                return new long[] { Convert.ToInt64(intValue) };
            }
            if (codeParameter.ParsedValue is uint uintValue)
            {
                return new long[] { Convert.ToInt64(uintValue) };
            }
            if (codeParameter.ParsedValue is float floatValue)
            {
                return new long[] { Convert.ToInt64(floatValue) };
            }
            if (codeParameter.ParsedValue is int[] intArray)
            {
                return intArray.Select(Convert.ToInt64).ToArray();
            }
            if (codeParameter.ParsedValue is int[] uintArray)
            {
                return uintArray.Select(Convert.ToInt64).ToArray();
            }
            if (codeParameter.ParsedValue is float[] floatArray)
            {
                return floatArray.Select(Convert.ToInt64).ToArray();
            }
            throw new InvalidParameterTypeException(codeParameter, typeof(long[]));
        }

        /// <summary>
        /// Explicit conversion operator to a driver ID array
        /// </summary>
        /// <param name="codeParameter">Target object</param>
        /// <returns>Converted value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        [return: NotNullIfNotNull(nameof(codeParameter))]
        public static explicit operator DriverId[]?(CodeParameter? codeParameter)
        {
            if (codeParameter is null)
            {
                return null;
            }
            if (codeParameter.ParsedValue is DriverId[] driverIdArray)
            {
                return driverIdArray;
            }
            if (codeParameter.ParsedValue is DriverId driverId)
            {
                return new DriverId[] { driverId };
            }
            if (codeParameter.ParsedValue is uint[] uintArray)
            {
                return uintArray.Select(value => new DriverId(value)).ToArray();
            }
            if (codeParameter.ParsedValue is uint uintValue)
            {
                return new DriverId[] { new DriverId(uintValue) };
            }
            throw new InvalidParameterTypeException(codeParameter, typeof(DriverId[]));
        }
        #endregion

        /// <summary>
        /// Equality operator
        /// </summary>
        /// <param name="a">Code parameter</param>
        /// <param name="b">Other object</param>
        /// <returns>True if both objects are equal</returns>
        public static bool operator ==(CodeParameter? a, object? b)
        {
            if (a is null || a.ParsedValue is null)
            {
                return b is null;
            }
            if (b is CodeParameter other)
            {
                return a.Letter.Equals(other.Letter) && a.ParsedValue.Equals(other.ParsedValue);
            }
            return a.ParsedValue.Equals(b);
        }

        /// <summary>
        /// Inequality operator
        /// </summary>
        /// <param name="a">Code parameter</param>
        /// <param name="b">Other object</param>
        /// <returns>True if both objects are not equal</returns>
        public static bool operator !=(CodeParameter? a, object? b) => !(a == b);

        /// <summary>
        /// Checks if the other obj equals this instance
        /// </summary>
        /// <param name="obj">Other object</param>
        /// <returns>True if both objects are not equal</returns>
        public override bool Equals(object? obj) => this == obj;

        /// <summary>
        /// Returns the hash code of this instance
        /// </summary>
        /// <returns>Computed hash code</returns>
        public override int GetHashCode() => Letter.GetHashCode() ^ (ParsedValue?.GetHashCode() ?? 0);

        /// <summary>
        /// Converts this parameter to a string
        /// </summary>
        /// <returns>String representation</returns>
        public override string ToString() => Letter + StringValue;
    }
    
    /// <summary>
    /// Converts a <see cref="CodeParameter"/> instance to JSON
    /// </summary>
    public sealed class CodeParameterConverter : JsonConverter<CodeParameter>
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
                char letter = '@';
                string propertyName = string.Empty;
                string? value = null;

                bool isString = false, isDriverId = false;

                while (reader.Read())
                {
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.PropertyName:
                            propertyName = reader.GetString()!;
                            break;

                        case JsonTokenType.String:
                            if (propertyName.Equals("letter", StringComparison.InvariantCultureIgnoreCase))
                            {
                                letter = Convert.ToChar(reader.GetString()!);
                            }
                            else if (propertyName.Equals("value", StringComparison.InvariantCultureIgnoreCase))
                            {
                                value = reader.GetString()!;
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
                            return new CodeParameter(letter, value ?? string.Empty, isString, isDriverId);
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
            writer.WriteString("value", value.StringValue);
            if (value.Type == typeof(DriverId) || value.Type == typeof(DriverId[]))
            {
                writer.WriteBoolean("isDriverId", true);
            }
            writer.WriteBoolean("isString", value.Type == typeof(string) && !value.IsExpression);
            writer.WriteEndObject();
        }
    }
}
