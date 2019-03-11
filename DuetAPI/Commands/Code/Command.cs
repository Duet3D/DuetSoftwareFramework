using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;

namespace DuetAPI.Commands
{
    /// <summary>
    /// A parsed representation of a generic G/M/T-code
    /// </summary>
    /// <seealso cref="CodeParameter"/>
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
        /// Major code number (e.g. 28 in G28)
        /// </summary>
        public int? MajorNumber { get; set; }
        
        /// <summary>
        /// Minor code number (e.g. 3 in G54.3)
        /// </summary>
        public int? MinorNumber { get; set; }
        
        /// <summary>
        /// List of parsed code parameters (see <see cref="CodeParameter"/> for further information)
        /// </summary>
        public List<CodeParameter> Parameters { get; } = new List<CodeParameter>();
        
        /// <summary>
        /// Comment of the G/M/T-code. Note that the parser combines different comment styles and appends them
        /// as a single value. So for example a code like 'G28 (Do homing) ; via G28' causes a comment like
        /// 'Do homing via G28' to be generated in this field
        /// </summary>
        public string Comment { get; set; }

        /// <summary>
        /// Indicates if the code has been preprocessed (see also <see cref="DuetAPI.Connection.ConnectionType.Intercept"/>)
        /// </summary>
        public bool IsPreProcessed { get; set; }
        
        /// <summary>
        /// Indicates if the code has been postprocessed (see also <see cref="DuetAPI.Connection.ConnectionType.Intercept"/>)
        /// </summary>
        public bool IsPostProcessed { get; set; }

        /// <summary>
        /// Retrieve the parameter whose letter equals c. Note that this look-up is case-sensitive!
        /// </summary>
        /// <param name="c">Letter of the code parameter to find</param>
        /// <returns>The parsed parameter instance or null if none could be found</returns>
        public CodeParameter GetParameter(char c) => Parameters.FirstOrDefault(p => p.Letter == 'C');

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
