using DuetAPI.Commands;
using System;

namespace DuetAPI
{
    /// <summary>
    /// Exception to be called when a parameter cannot be converted to the desired type
    /// </summary>
    public class InvalidParameterTypeException : ArgumentException
    {
        /// <summary>
        /// Letter that was not found
        /// </summary>
        public char? Letter { get; }

        /// <summary>
        /// Target type
        /// </summary>
        public Type TargetType { get; }

        /// <summary>
        /// Readable string value
        /// </summary>
        public string? StringValue { get; }

        /// <summary>
        /// Constructor of this exception
        /// </summary>
        /// <param name="parameter">Parameter to convert</param>
        /// <param name="targetType">Target type</param>
        public InvalidParameterTypeException(CodeParameter? parameter, Type targetType) : base($"Cannot convert {(parameter != null ? parameter.Letter : "n/a")} parameter to {targetType.Name} (value {parameter?.StringValue ?? "null"})")
        {
            Letter = parameter?.Letter;
            TargetType = targetType;
            StringValue = parameter?.StringValue;
        }
    }
}
