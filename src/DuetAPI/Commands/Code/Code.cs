using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// A parsed representation of a generic G/M/T-code
    /// </summary>
    [RequiredPermissions(SbcPermissions.CommandExecution)]
    public partial class Code : Command<Message?>
    {
        /// <summary>
        /// Create an empty Code representation
        /// </summary>
        public Code() { }

        /// <summary>
        /// Create a new Code instance and attempt to parse the given code string
        /// </summary>
        /// <param name="code">UTF8-encoded G/M/T-Code</param>
        public Code(string code)
        {
            using MemoryStream stream = new(Encoding.UTF8.GetBytes(code));
            using StreamReader reader = new(stream);
            Parse(reader, this);
        }

        /// <summary>
        /// The connection ID this code was received from. If this is 0, the code originates from an internal DCS task
        /// </summary>
        /// <remarks>
        /// Usually there is no need to populate this property. It is internally overwritten by the control server on receipt
        /// </remarks>
        public int SourceConnection { get; set; }

        /// <summary>
        /// Result of this code. This property is only set when the code has finished its excution.
        /// It remains null if the code has been cancelled
        /// </summary>
        /// <remarks>
        /// This used to be of type CodeResult but since v3.2 CodeResult can read Message JSON so it should remain compatible
        /// </remarks>
        public Message? Result { get; set; }

        /// <summary>
        /// Type of the code
        /// </summary>
        public CodeType Type { get; set; } = CodeType.None;

        /// <summary>
        /// Code channel to send this code to
        /// </summary>
        public CodeChannel Channel { get; set; } = Defaults.InputChannel;

        /// <summary>
        /// Check if this code is from a file channel
        /// </summary>
        [JsonIgnore]
        public bool IsFromFileChannel { get => Channel == CodeChannel.File || Channel == CodeChannel.File2; }

        /// <summary>
        /// Line number of this code
        /// </summary>
        public long? LineNumber { get; set; }

        /// <summary>
        /// Number of whitespaces prefixing the command content
        /// </summary>
        public byte Indent { get; set; }

        /// <summary>
        /// Type of conditional G-code (if any)
        /// </summary>
        public KeywordType Keyword { get; set; } = KeywordType.None;

        /// <summary>
        /// Argument of the conditional G-code (if any)
        /// </summary>
        public string? KeywordArgument { get; set; }

        /// <summary>
        /// Major code number (e.g. 28 in G28)
        /// </summary>
        public int? MajorNumber { get; set; }

        /// <summary>
        /// Minor code number (e.g. 3 in G54.3)
        /// </summary>
        public sbyte? MinorNumber { get; set; }

        /// <summary>
        /// Flags of this code
        /// </summary>
        public CodeFlags Flags { get; set; } = CodeFlags.None;

        /// <summary>
        /// Comment of the G/M/T-code. May be null if no comment is present
        /// </summary>
        /// <remarks>
        /// The parser combines different comment segments and concatenates them as a single value.
        /// So for example a code like 'G28 (Do homing) ; via G28' causes the Comment field to be filled with 'Do homing via G28'
        /// </remarks>
        public string? Comment { get; set; }

        /// <summary>
        /// File position of this code in bytes (optional)
        /// </summary>
        public long? FilePosition { get; set; }

        /// <summary>
        /// Length of the original code in bytes (optional)
        /// </summary>
        public int? Length { get; set; }

        /// <summary>
        /// List of parsed code parameters (see <see cref="CodeParameter"/> for further information)
        /// </summary>
        /// <seealso cref="CodeParameter"/>
        public List<CodeParameter> Parameters { get; set; } = [];

        /// <summary>
        /// Copy the properties of another code instance. This is used when a code is rewritten
        /// </summary>
        /// <param name="code"></param>
        public void CopyFrom(Code code)
        {
            Type = code.Type;
            LineNumber = code.LineNumber;
            Indent = code.Indent;
            Keyword = code.Keyword;
            KeywordArgument = code.KeywordArgument;
            MajorNumber = code.MajorNumber;
            MinorNumber = code.MinorNumber;
            Flags = code.Flags;
            Comment = code.Comment;
            FilePosition = code.FilePosition;
            Length = code.Length;
            Parameters = code.Parameters;
        }

        /// <summary>
        /// Reset this instance
        /// </summary>
        public virtual void Reset()
        {
            SourceConnection = 0;
            Result = null;
            Type = CodeType.None;
            Channel = Defaults.InputChannel;
            LineNumber = null;
            Indent = 0;
            Keyword = KeywordType.None;
            KeywordArgument = null;
            MajorNumber = MinorNumber = null;
            Flags = CodeFlags.None;
            Comment = null;
            FilePosition = Length = null;
            Length = null;
            Parameters.Clear();
        }

        /// <summary>
        /// Retrieve the parameter whose letter equals c
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <returns>The parsed parameter instance or null if none could be found</returns>
        [Obsolete("Use GetParameter or one of the typed getter methods instead")]
        public CodeParameter? Parameter(char letter) => GetParameter(letter);

        /// <summary>
        /// Retrieve the parameter whose letter equals c or generate a default parameter
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="defaultValue">Default parameter value (no expression)</param>
        /// <returns>The parsed parameter instance or null if none could be found</returns>
        [Obsolete("Use GetParameter or one of the typed getter methods instead")]
        public CodeParameter Parameter(char letter, object defaultValue) => GetParameter(letter, defaultValue);

        /// <summary>
        /// Check if a given parameter exists
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <returns>If the parameter is present</returns>
        public bool HasParameter(char letter) => Parameters.Any(p => p.Letter == letter);

        /// <summary>
        /// Retrieve the parameter whose letter equals c
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <returns>The parsed parameter instance or null if none could be found</returns>
        public CodeParameter? GetParameter(char letter) => Parameters.FirstOrDefault(p => p.Letter == letter);

        /// <summary>
        /// Retrieve the parameter whose letter equals c or generate a default parameter
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="defaultValue">Default parameter value (no expression)</param>
        /// <returns>The parsed parameter instance or null if none could be found</returns>
        public CodeParameter GetParameter(char letter, object defaultValue) => GetParameter(letter) ?? new CodeParameter(letter, defaultValue);

        /// <summary>
        /// Try to get a parameter by letter
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="parameter">Parameter if found, else null</param>
        /// <returns>True if the requested parameter could be found</returns>
        public bool TryGetParameter(char letter, [NotNullWhen(true)] out CodeParameter? parameter)
        {
            foreach (CodeParameter param in Parameters)
            {
                if (param.Letter == letter && !param.IsExpression)
                {
                    parameter = param;
                    return true;
                }
            }
            parameter = null;
            return false;
        }

        /// <summary>
        /// Get a float parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="MissingParameterException">Parameter not found</exception>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public float GetFloat(char letter)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (float)param : throw new MissingParameterException(letter);
        }

        /// <summary>
        /// Get a float parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="defaultValue">Default value to return if no parameter could be found</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public float GetFloat(char letter, float defaultValue)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (float)param : defaultValue;
        }

        /// <summary>
        /// Try to get a float parameter value by letter
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="parameter">Parameter if found, else default</param>
        /// <returns>True if the requested parameter could be found</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public bool TryGetFloat(char letter, out float parameter)
        {
            foreach (CodeParameter param in Parameters)
            {
                if (param.Letter == letter && !param.IsExpression)
                {
                    parameter = (float)param;
                    return true;
                }
            }
            parameter = default;
            return false;
        }

        /// <summary>
        /// Try to get a float parameter value by letter
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="parameter">Parameter if found, else null</param>
        /// <returns>True if the requested parameter could be found</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public bool TryGetFloat(char letter, out float? parameter)
        {
            foreach (CodeParameter param in Parameters)
            {
                if (param.Letter == letter && !param.IsExpression)
                {
                    parameter = (float)param;
                    return true;
                }
            }
            parameter = null;
            return false;
        }

        /// <summary>
        /// Get an integer parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="MissingParameterException">Parameter not found</exception>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public int GetInt(char letter)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (int)param : throw new MissingParameterException(letter);
        }

        /// <summary>
        /// Get an integer parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="defaultValue">Default value to return if no parameter could be found</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public int GetInt(char letter, int defaultValue)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (int)param : defaultValue;
        }

        /// <summary>
        /// Try to get an integer parameter value by letter
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="parameter">Parameter if found, else default</param>
        /// <returns>True if the requested parameter could be found</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public bool TryGetInt(char letter, out int parameter)
        {
            foreach (CodeParameter param in Parameters)
            {
                if (param.Letter == letter && !param.IsExpression)
                {
                    parameter = (int)param;
                    return true;
                }
            }
            parameter = default;
            return false;
        }

        /// <summary>
        /// Try to get an integer parameter value by letter
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="parameter">Parameter if found, else null</param>
        /// <returns>True if the requested parameter could be found</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public bool TryGetInt(char letter, out int? parameter)
        {
            foreach (CodeParameter param in Parameters)
            {
                if (param.Letter == letter && !param.IsExpression)
                {
                    parameter = (int?)param;
                    return true;
                }
            }
            parameter = null;
            return false;
        }

        /// <summary>
        /// Get an unsigned integer parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="MissingParameterException">Parameter not found</exception>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public uint GetUInt(char letter)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (uint)param : throw new MissingParameterException(letter);
        }

        /// <summary>
        /// Get an unsigned integer parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="defaultValue">Default value to return if no parameter could be found</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public uint GetUInt(char letter, uint defaultValue)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (uint)param : defaultValue;
        }

        /// <summary>
        /// Try to get an unsigned integer parameter value by letter
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="parameter">Parameter if found, else default</param>
        /// <returns>True if the requested parameter could be found</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public bool TryGetUInt(char letter, out uint parameter)
        {
            foreach (CodeParameter param in Parameters)
            {
                if (param.Letter == letter && !param.IsExpression)
                {
                    parameter = (uint)param;
                    return true;
                }
            }
            parameter = default;
            return false;
        }

        /// <summary>
        /// Try to get an unsigned integer parameter value by letter
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="parameter">Parameter if found, else null</param>
        /// <returns>True if the requested parameter could be found</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public bool TryGetUInt(char letter, out uint? parameter)
        {
            foreach (CodeParameter param in Parameters)
            {
                if (param.Letter == letter && !param.IsExpression)
                {
                    parameter = (uint)param;
                    return true;
                }
            }
            parameter = null;
            return false;
        }

        /// <summary>
        /// Get a long parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="MissingParameterException">Parameter not found</exception>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public long GetLong(char letter)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (long)param : throw new MissingParameterException(letter);
        }

        /// <summary>
        /// Get a long parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="defaultValue">Default value to return if no parameter could be found</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public long GetLong(char letter, long defaultValue)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (long)param : defaultValue;
        }

        /// <summary>
        /// Try to get a long parameter value by letter
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="parameter">Parameter if found, else default</param>
        /// <returns>True if the requested parameter could be found</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public bool TryGetLong(char letter, out long parameter)
        {
            foreach (CodeParameter param in Parameters)
            {
                if (param.Letter == letter && !param.IsExpression)
                {
                    parameter = (long)param;
                    return true;
                }
            }
            parameter = default;
            return false;
        }

        /// <summary>
        /// Try to get a long parameter value by letter
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="parameter">Parameter if found, else null</param>
        /// <returns>True if the requested parameter could be found</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public bool TryGetLong(char letter, out long? parameter)
        {
            foreach (CodeParameter param in Parameters)
            {
                if (param.Letter == letter && !param.IsExpression)
                {
                    parameter = (long)param;
                    return true;
                }
            }
            parameter = null;
            return false;
        }

        /// <summary>
        /// Get a boolean parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="MissingParameterException">Parameter not found</exception>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public bool GetBool(char letter)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (bool)param : throw new MissingParameterException(letter);
        }

        /// <summary>
        /// Get a boolean parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="defaultValue">Default value to return if no parameter could be found</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public bool GetBool(char letter, bool defaultValue)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (bool)param : defaultValue;
        }

        /// <summary>
        /// Try to get a long parameter value by letter
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="parameter">Parameter if found, else default</param>
        /// <returns>True if the requested parameter could be found</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public bool TryGetBool(char letter, out bool parameter)
        {
            foreach (CodeParameter param in Parameters)
            {
                if (param.Letter == letter && !param.IsExpression)
                {
                    parameter = (bool)param;
                    return true;
                }
            }
            parameter = default;
            return false;
        }

        /// <summary>
        /// Try to get a long parameter value by letter
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="parameter">Parameter if found, else null</param>
        /// <returns>True if the requested parameter could be found</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public bool TryGetBool(char letter, out bool? parameter)
        {
            foreach (CodeParameter param in Parameters)
            {
                if (param.Letter == letter && !param.IsExpression)
                {
                    parameter = (bool)param;
                    return true;
                }
            }
            parameter = null;
            return false;
        }

        /// <summary>
        /// Get a string parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="MissingParameterException">Parameter not found</exception>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public string GetString(char letter)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (string)param : throw new MissingParameterException(letter);
        }

        /// <summary>
        /// Get a string parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <returns>Parameter value or null</returns>
        public string? GetOptionalString(char letter)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (string)param : null;
        }

        /// <summary>
        /// Get an unsigned integer parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="defaultValue">Default value to return if no parameter could be found</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public string GetString(char letter, string defaultValue)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (string)param : defaultValue;
        }

        /// <summary>
        /// Try to get a string parameter value by letter
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="parameter">Parameter if found, else null</param>
        /// <returns>True if the requested parameter could be found</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public bool TryGetString(char letter, [NotNullWhen(true)] out string? parameter)
        {
            foreach (CodeParameter param in Parameters)
            {
                if (param.Letter == letter && !param.IsExpression)
                {
                    parameter = (string)param;
                    return true;
                }
            }
            parameter = null;
            return false;
        }

        /// <summary>
        /// Get a driver ID parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="MissingParameterException">Parameter not found</exception>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public DriverId GetDriverId(char letter)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (DriverId)param : throw new MissingParameterException(letter);
        }

        /// <summary>
        /// Get a driver ID parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="defaultValue">Default value to return if no parameter could be found</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public DriverId GetDriverId(char letter, DriverId defaultValue)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (DriverId)param : defaultValue;
        }

        /// <summary>
        /// Try to get a driver ID parameter value by letter
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="parameter">Parameter if found, else null</param>
        /// <returns>True if the requested parameter could be found</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public bool TryGetDriverId(char letter, [NotNullWhen(true)] out DriverId? parameter)
        {
            foreach (CodeParameter param in Parameters)
            {
                if (param.Letter == letter && !param.IsExpression)
                {
                    parameter = (DriverId)param;
                    return true;
                }
            }
            parameter = null;
            return false;
        }

        /// <summary>
        /// Get an IP address parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="MissingParameterException">Parameter not found</exception>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public IPAddress GetIPAddress(char letter)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (IPAddress)param : throw new MissingParameterException(letter);
        }

        /// <summary>
        /// Get an IP address parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="defaultValue">Default value to return if no parameter could be found</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public IPAddress GetIPAddress(char letter, IPAddress defaultValue)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (IPAddress)param : defaultValue;
        }

        /// <summary>
        /// Try to get a driver ID parameter value by letter
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="parameter">Parameter if found, else null</param>
        /// <returns>True if the requested parameter could be found</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public bool TryGetIPAddress(char letter, [NotNullWhen(true)] out IPAddress? parameter)
        {
            foreach (CodeParameter param in Parameters)
            {
                if (param.Letter == letter && !param.IsExpression)
                {
                    parameter = (IPAddress)param;
                    return true;
                }
            }
            parameter = null;
            return false;
        }

        /// <summary>
        /// Get a float array parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="MissingParameterException">Parameter not found</exception>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public float[] GetFloatArray(char letter)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (float[])param : throw new MissingParameterException(letter);
        }

        /// <summary>
        /// Get a float array parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="defaultValue">Default value to return if no parameter could be found</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public float[] GetFloatArray(char letter, float[] defaultValue)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (float[])param : defaultValue;
        }

        /// <summary>
        /// Try to get a float array parameter value by letter
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="parameter">Parameter if found, else null</param>
        /// <returns>True if the requested parameter could be found</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public bool TryGetFloatArray(char letter, [NotNullWhen(true)] out float[]? parameter)
        {
            foreach (CodeParameter param in Parameters)
            {
                if (param.Letter == letter && !param.IsExpression)
                {
                    parameter = (float[])param;
                    return true;
                }
            }
            parameter = null;
            return false;
        }

        /// <summary>
        /// Get an integer array parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="MissingParameterException">Parameter not found</exception>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public int[] GetIntArray(char letter)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (int[])param : throw new MissingParameterException(letter);
        }

        /// <summary>
        /// Get an integer array parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="defaultValue">Default value to return if no parameter could be found</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public int[] GetIntArray(char letter, int[] defaultValue)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (int[])param : defaultValue;
        }

        /// <summary>
        /// Try to get an integer array parameter value by letter
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="parameter">Parameter if found, else null</param>
        /// <returns>True if the requested parameter could be found</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public bool TryGetIntArray(char letter, [NotNullWhen(true)] out int[]? parameter)
        {
            foreach (CodeParameter param in Parameters)
            {
                if (param.Letter == letter && !param.IsExpression)
                {
                    parameter = (int[])param;
                    return true;
                }
            }
            parameter = null;
            return false;
        }

        /// <summary>
        /// Get an unsigned integer array parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="MissingParameterException">Parameter not found</exception>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public uint[] GetUIntArray(char letter)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (uint[])param : throw new MissingParameterException(letter);
        }

        /// <summary>
        /// Get an unsigned integer array parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="defaultValue">Default value to return if no parameter could be found</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public uint[] GetUIntArray(char letter, uint[] defaultValue)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (uint[])param : defaultValue;
        }

        /// <summary>
        /// Try to get an unsigned integer array parameter value by letter
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="parameter">Parameter if found, else null</param>
        /// <returns>True if the requested parameter could be found</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public bool TryGetUIntArray(char letter, [NotNullWhen(true)] out uint[]? parameter)
        {
            foreach (CodeParameter param in Parameters)
            {
                if (param.Letter == letter && !param.IsExpression)
                {
                    parameter = (uint[])param;
                    return true;
                }
            }
            parameter = null;
            return false;
        }

        /// <summary>
        /// Get a long array parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="MissingParameterException">Parameter not found</exception>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public long[] GetLongArray(char letter)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (long[])param : throw new MissingParameterException(letter);
        }

        /// <summary>
        /// Get a long array parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="defaultValue">Default value to return if no parameter could be found</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public long[] GetLongArray(char letter, long[] defaultValue)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (long[])param : defaultValue;
        }

        /// <summary>
        /// Try to get a long array parameter value by letter
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="parameter">Parameter if found, else null</param>
        /// <returns>True if the requested parameter could be found</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public bool TryGetLongArray(char letter, [NotNullWhen(true)] out long[]? parameter)
        {
            foreach (CodeParameter param in Parameters)
            {
                if (param.Letter == letter && !param.IsExpression)
                {
                    parameter = (long[])param;
                    return true;
                }
            }
            parameter = null;
            return false;
        }

        /// <summary>
        /// Get a driver ID array parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="MissingParameterException">Parameter not found</exception>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public DriverId[] GetDriverIdArray(char letter)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (DriverId[])param : throw new MissingParameterException(letter);
        }

        /// <summary>
        /// Get a driver ID array parameter value
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="defaultValue">Default value to return if no parameter could be found</param>
        /// <returns>Parameter value</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public DriverId[] GetDriverIdArray(char letter, DriverId[] defaultValue)
        {
            CodeParameter? param = GetParameter(letter);
            return (param is not null) ? (DriverId[])param : defaultValue;
        }

        /// <summary>
        /// Try to get a long array parameter value by letter
        /// </summary>
        /// <param name="letter">Letter of the parameter to find</param>
        /// <param name="parameter">Parameter if found, else null</param>
        /// <returns>True if the requested parameter could be found</returns>
        /// <exception cref="InvalidParameterTypeException">Failed to convert parameter value</exception>
        public bool TryGetDriverIdArray(char letter, [NotNullWhen(true)] out DriverId[]? parameter)
        {
            foreach (CodeParameter param in Parameters)
            {
                if (param.Letter == letter && !param.IsExpression)
                {
                    parameter = (DriverId[])param;
                    return true;
                }
            }
            parameter = null;
            return false;
        }

        /// <summary>
        /// Reconstruct an unprecedented string from the parameter list or
        /// retrieve the parameter which does not have a letter assigned
        /// </summary>
        /// <param name="quoteStrings">Encapsulate strings in double quotes</param>
        /// <returns>Unprecedented string</returns>
        /// <remarks>
        /// If no parameter is present, an empty string is returned
        /// </remarks>
        public string GetUnprecedentedString(bool quoteStrings = false)
        {
            foreach (CodeParameter p in Parameters)
            {
                if (p.Letter == '@')
                {
                    return quoteStrings ? $"\"{p.StringValue.Replace("\"", "\"\"")}\"" : p.StringValue;
                }
            }

            StringBuilder builder = new();
            foreach (CodeParameter p in Parameters)
            {
                if (builder.Length is not 0)
                {
                    builder.Append(' ');
                }

                builder.Append(p.Letter);
                if (quoteStrings && !p.IsExpression && p.Type == typeof(string))
                {
                    builder.Append('"');
                    builder.Append(quoteStrings ? p.StringValue.Replace("\"", "\"\"") : p.StringValue);
                    builder.Append('"');
                }
                else
                {
                    builder.Append(p.StringValue);
                }
            }
            return builder.ToString();
        }

        /// <summary>
        /// Convert the parsed code back to a text-based G/M/T-code
        /// </summary>
        /// <returns>Reconstructed code string</returns>
        public override string ToString()
        {
            if (Keyword is not KeywordType.None)
            {
                string asString = KeywordToString() + ((KeywordArgument is null) ? string.Empty : " " + KeywordArgument);
                if (Result is not null && !string.IsNullOrEmpty(Result.Content))
                {
                    asString += " => ";
                    asString += Result.ToString().TrimEnd();
                }
                return asString;
            }

            if (Type == CodeType.Comment)
            {
                return ";" + Comment;
            }

            // Because it is neither always feasible nor reasonable to keep track of the original code,
            // attempt to rebuild it here. First, assemble the code letter, then the major+minor numbers (e.g. G53.4)
            StringBuilder builder = new();
            builder.Append(ToShortString());

            // After this append each parameter and encapsulate it in double quotes
            foreach (CodeParameter parameter in Parameters)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                if (parameter.Letter is not '@')
                {
                    builder.Append(parameter.Letter);
                }

                if (parameter.ParsedValue is not null)
                {
                    if (parameter.Type == typeof(string) && !parameter.IsExpression)
                    {
                        builder.Append('"');
                        builder.Append(parameter.StringValue.Replace("\"", "\"\""));
                        builder.Append('"');
                    }
                    else
                    {
                        builder.Append(parameter.StringValue);
                    }
                }
            }

            // Then the comment is appended (if applicable)
            if (!string.IsNullOrEmpty(Comment))
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(';');
                builder.Append(Comment);
            }

            // If this code has finished, append the code result
            if (Result is not null && !string.IsNullOrEmpty(Result.Content))
            {
                builder.Append(" => ");
                builder.Append(Result.ToString().TrimEnd());
            }

            return builder.ToString();
        }

        /// <summary>
        /// Convert only the command portion to a text-based G/M/T-code (e.g. G28)
        /// </summary>
        /// <returns>Command fraction of the code</returns>
        public string ToShortString()
        {
            if (Keyword is not KeywordType.None)
            {
                return KeywordToString();
            }

            if (Type == CodeType.None)
            {
                return string.Empty;
            }

            if (Type == CodeType.Comment)
            {
                return "(comment)";
            }

            string prefix = Flags.HasFlag(CodeFlags.EnforceAbsolutePosition) ? "G53 " : string.Empty;
            if (MajorNumber is not null)
            {
                if (MinorNumber is not null)
                {
                    return prefix + $"{(char)Type}{MajorNumber}.{MinorNumber}";
                }
                return prefix + $"{(char)Type}{MajorNumber}";
            }
            return prefix + $"{(char)Type}";
        }

        /// <summary>
        /// Convert the keyword to a string
        /// </summary>
        /// <returns></returns>
        private string KeywordToString()
        {
            return Keyword switch
            {
                KeywordType.If => "if",
                KeywordType.ElseIf => "elif",
                KeywordType.Else => "else",
                KeywordType.While => "while",
                KeywordType.Break => "break",
                KeywordType.Continue => "continue",
                KeywordType.Abort => "abort",
                KeywordType.Var => "var",
                KeywordType.Set => "set",
                KeywordType.Echo => "echo",
                KeywordType.Global => "global",
                _ => throw new NotImplementedException(),
            };
        }
    }
}
