using System.Collections.Generic;
using System.Linq;
using DuetAPI.Connection;

namespace DuetAPI.Commands
{
    /// <summary>
    /// A parsed representation of a generic G/M/T-code
    /// Line number support has not yet been implemented (TODO)
    /// </summary>
    /// <seealso cref="CodeResult"/>
    public partial class Code : Command<CodeResult>
    {
        /// <summary>
        /// Create an empty Code representation
        /// </summary>
        public Code() { }

        /// <summary>
        /// Type of the code. If no exact type could be determined, it is interpreted as a comment
        /// </summary>
        public CodeType Type { get; set; } = CodeType.Comment;

        /// <summary>
        /// Code channel to send this code to
        /// </summary>
        public CodeChannel Channel { get; set; } = CodeChannel.SPI;

        /// <summary>
        /// Major code number (e.g. 28 in G28)
        /// </summary>
        public int? MajorNumber { get; set; }
        
        /// <summary>
        /// Minor code number (e.g. 3 in G54.3)
        /// </summary>
        public int? MinorNumber { get; set; }
        
        /// <summary>
        /// Whether absolute coordinates must be used regardless of the current mode (see G53)
        /// </summary>
        public bool EnforceAbsoluteCoordinates { get; set; }
        
        /// <summary>
        /// List of parsed code parameters (see <see cref="CodeParameter"/> for further information)
        /// </summary>
        /// <seealso cref="CodeParameter"/>
        public List<CodeParameter> Parameters { get; } = new List<CodeParameter>();
        
        /// <summary>
        /// Comment of the G/M/T-code
        /// </summary>
        /// <remarks>
        /// The parser combines different comment segments and concatenates them as a single value.
        /// So for example a code like 'G28 (Do homing) ; via G28' causes the Comment field to be filled with 'Do homing via G28'
        /// </remarks>
        public string Comment { get; set; }

        /// <summary>
        /// Indicates if the code has been preprocessed (see also <see cref="ConnectionMode.Intercept"/>)
        /// </summary>
        public bool IsPreProcessed { get; set; }
        
        /// <summary>
        /// Indicates if the code has been postprocessed (see also <see cref="ConnectionMode.Intercept"/>)
        /// </summary>
        public bool IsPostProcessed { get; set; }

        /// <summary>
        /// Indicates if the code comes from a macro file
        /// </summary>
        public bool IsFromMacro { get; set; }

        /// <summary>
        /// File position in bytes (optional)
        /// </summary>
        public long? FilePosition { get; set; }
        
        /// <summary>
        /// Retrieve the parameter whose letter equals c. Note that this look-up is case-sensitive!
        /// </summary>
        /// <param name="c">Letter of the code parameter to find</param>
        /// <returns>The parsed parameter instance or null if none could be found</returns>
        public CodeParameter GetParameter(char c) => Parameters.FirstOrDefault(p => p.Letter == 'C');

        /// <summary>
        /// Reconstruct an unprecedented string from the parameter list
        /// </summary>
        /// <param name="quoteStrings">Encapsulate strings in double quotes</param>
        /// <returns>Unprecedented string</returns>
        public string GetUnprecedentedString(bool quoteStrings = false)
        {
            string result = "";
            foreach (CodeParameter p in Parameters)
            {
                if (result != "")
                {
                    result += " ";
                }
                if (p.Letter != '\0')
                {
                    result += p.Letter;
                }
                if (quoteStrings && p.Type == typeof(string))
                {
                    result += '"';
                }
                result += p.AsString;
                if (quoteStrings && p.Type == typeof(string))
                {
                    result += '"';
                }
            }
            return result;
        }

        /// <summary>
        /// Convert the parsed code back to a text-based G/M/T-code
        /// </summary>
        /// <returns>Reconstructed code string</returns>
        public override string ToString()
        {
            if (Type == CodeType.Comment)
            {
                return (Comment == null) ? "" : (";" + Comment);
            }

            // Because it is neither always feasible nor reasonable to keep track of the original code,
            // attempt to rebuild it here. First, assemble the code letter, then the major+minor numbers (e.g. G53.4)
            string result = ToShortString();

            // After this append each parameter and encapsulate it in double quotes
            foreach(CodeParameter parameter in Parameters)
            {
                if (parameter.Type == typeof(string))
                {
                    result += $" {parameter.Letter}\"{parameter.AsString.Replace("\"", "\"\"")}\"";
                }
                else
                {
                    result += $" {parameter.Letter}{parameter.AsString}";
                }
            }

            // Finally the comment is appended
            if (Comment != null)
            {
                result += " ;" + Comment;
            }
            return result;
        }

        /// <summary>
        /// Convert only the command portion to a text-based G/M/T-code (e.g. G28)
        /// </summary>
        /// <returns>Command fraction of the code</returns>
        public string ToShortString()
        {
            if (Type == CodeType.Comment)
            {
                return "(comment)";
            }

            if (MinorNumber.HasValue)
            {
                return $"{(char)Type}{MajorNumber}.{MinorNumber}";
            }
            return $"{(char)Type}{MajorNumber}";
        }
    }
}
