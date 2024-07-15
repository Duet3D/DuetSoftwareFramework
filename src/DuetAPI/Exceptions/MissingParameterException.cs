using System;

namespace DuetAPI
{
    /// <summary>
    /// Exception to be called when a parameter is required but not found
    /// </summary>
    /// <param name="letter">Letter of the parameter that could not be found</param>
    public class MissingParameterException(char letter) : ArgumentException($"Missing {letter} parameter")
    {
        /// <summary>
        /// Letter that was not found
        /// </summary>
        public char Letter { get; } = letter;
    }
}
