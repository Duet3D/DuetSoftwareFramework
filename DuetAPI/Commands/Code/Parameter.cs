using Newtonsoft.Json;
using System.Globalization;

namespace DuetAPI.Commands
{
    public class CodeParameter
    {
        public char Letter { get; set; }
        public string Value { get; set; }

        public CodeParameter(char letter, string value)
        {
            Letter = letter;
            Value = value;
        }

        [JsonIgnore]
        public int AsInt
        {
            get
            {
                if (int.TryParse(Value.Trim(), out int result))
                {
                    return result;
                }
                throw new CodeParserException($"Failed to convert {Letter} parameter  to integer (value {Value})");
            }
        }

        [JsonIgnore]
        public double AsDouble
        {
            get
            {
                if (double.TryParse(Value.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
                {
                    return result;
                }
                throw new CodeParserException($"Failed to convert {Letter} parameter to double (value {Value})");
            }
        }
    }
}
