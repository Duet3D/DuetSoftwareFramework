using DuetAPI.Commands;
using System;

namespace DuetAPI
{
    /// <summary>
    /// Exception to be called when a parameter cannot be converted to the desired type
    /// </summary>
    /// <param name="parameter">Parameter to convert</param>
    /// <param name="targetType">Target type</param>
    public class InvalidParameterTypeException(CodeParameter? parameter, Type targetType) : ArgumentException($"Cannot convert {(parameter != null ? parameter.Letter : "n/a")} parameter to {targetType.Name} (value {parameter?.StringValue ?? "null"})")
    {
        /// <summary>
        /// Letter that was not found
        /// </summary>
        public char? Letter { get; } = parameter?.Letter;

        /// <summary>
        /// Target type
        /// </summary>
        public Type TargetType { get; } = targetType;

        /// <summary>
        /// Readable string value
        /// </summary>
        public string? StringValue { get; } = parameter?.StringValue;
    }
}
