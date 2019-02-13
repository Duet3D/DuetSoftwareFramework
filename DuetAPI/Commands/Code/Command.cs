using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Collections.Generic;
using System.Linq;

namespace DuetAPI.Commands
{
    public enum CodeType
    {
        Comment = 'C',
        GCode = 'G',
        MCode = 'M',
        TCode = 'T'
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum CodeSource
    {
        Generic,
        File,
        HTTP,
        Telnet
    }

    public partial class Code : Command<CodeResult>
    {
        public CodeType Type { get; set; } = CodeType.Comment; 
        public int? MajorNumber { get; set; }
        public int? MinorNumber { get; set; }
        public List<CodeParameter> Parameters { get; } = new List<CodeParameter>();
        public string Comment { get; set; }

        public CodeSource Source { get; set; } = CodeSource.Generic;
        public bool IsPreProcessed { get; set; }
        public bool IsPostProcessed { get; set; }

        public Code() { }

        public CodeParameter GetParameter(char c) => Parameters.FirstOrDefault(p => p.Letter == 'C');

        // Return a reconstructed text-based representation of the parsed G/M/T-code
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
                bool containsSpacesOrQuotes = false;
                foreach(char c in parameter.Value)
                {
                    if (char.IsWhiteSpace(c) || c == '"')
                    {
                        containsSpacesOrQuotes = true;
                        break;
                    }
                }

                if (containsSpacesOrQuotes)
                {
                    result += $" {parameter.Letter}\"{parameter.Value.Replace("\"", "\"\"")}\"";
                }
                else
                {
                    result += $" {parameter.Letter}{parameter.Value}";
                }
            }

            // Finally the comment is appended
            if (Comment != null)
            {
                result += " ;" + Comment;
            }
            return result;
        }

        // Get only the command portion of the code (e.g. G53.4)
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
