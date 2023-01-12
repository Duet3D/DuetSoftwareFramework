using System;

namespace DuetAPI
{
    /// <summary>
    /// Exception to be called when a parameter is required but not found
    /// </summary>
    public class MissingParameterException : ArgumentException
    {
        /// <summary>
        /// Letter that was not found
        /// </summary>
        public char Letter { get; }

        /// <summary>
        /// Constructor of this exception
        /// </summary>
        /// <param name="letter">Letter of the parameter that could not be found</param>
        public MissingParameterException(char letter) : base($"Missing {letter} parameter") => Letter = letter;
    }
}
