using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DuetAPI.Connection;
using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// A parsed representation of a generic G/M/T-code
    /// </summary>
    [RequiredPermissions(SbcPermissions.CommandExecution)]
    public partial class Code : Command<CodeResult>
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
        public CodeResult Result { get; set; }

        /// <summary>
        /// Type of the code. If no exact type could be determined, it is interpreted as a comment
        /// </summary>
        public CodeType Type { get; set; } = CodeType.Comment;

        /// <summary>
        /// Code channel to send this code to
        /// </summary>
        public CodeChannel Channel { get; set; } = Defaults.InputChannel;

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
        public string KeywordArgument { get; set; }

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
        public string Comment { get; set; }

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
        public List<CodeParameter> Parameters { get; set; } = new List<CodeParameter>();

        /// <summary>
        /// Reset this instance
        /// </summary>
        public virtual void Reset()
        {
            SourceConnection = 0;
            Result = null;
            Type = CodeType.Comment;
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
        /// Retrieve the parameter whose letter equals c. Note that this look-up is case-insensitive
        /// </summary>
        /// <param name="c">Letter of the parameter to find</param>
        /// <returns>The parsed parameter instance or null if none could be found</returns>
        public CodeParameter Parameter(char c)
        {
            c = char.ToUpperInvariant(c);
            return Parameters.FirstOrDefault(p => char.ToUpperInvariant(p.Letter) == c);
        }

        /// <summary>
        /// Retrieve the parameter whose letter equals c or generate a default parameter
        /// </summary>
        /// <param name="c">Letter of the parameter to find</param>
        /// <param name="defaultValue">Default parameter value (no expression)</param>
        /// <returns>The parsed parameter instance or null if none could be found</returns>
        public CodeParameter Parameter(char c, object defaultValue) => Parameter(c) ?? new CodeParameter(c, defaultValue);

        /// <summary>
        /// Reconstruct an unprecedented string from the parameter list or
        /// retrieve the parameter which does not have a letter assigned
        /// </summary>
        /// <param name="quoteStrings">Encapsulate strings in double quotes</param>
        /// <returns>Unprecedented string</returns>
        public string GetUnprecedentedString(bool quoteStrings = false)
        {
            foreach (CodeParameter p in Parameters)
            {
                if (p.Letter == '@')
                {
                    return p;
                }
            }

            StringBuilder builder = new();
            foreach (CodeParameter p in Parameters)
            {
                if (builder.Length != 0)
                {
                    builder.Append(' ');
                }
                builder.Append(p.Letter);
                if (quoteStrings && p.Type == typeof(string))
                {
                    builder.Append('"');
                }
                builder.Append((string)p);
                if (quoteStrings && p.Type == typeof(string))
                {
                    builder.Append('"');
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
            if (Keyword != KeywordType.None)
            {
                return KeywordToString() + ((KeywordArgument == null) ? string.Empty : " " + KeywordArgument);
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
            foreach(CodeParameter parameter in Parameters)
            {
                if (parameter.Letter != '@')
                {
                    if (parameter.Type == typeof(string) && !parameter.IsExpression)
                    {
                        builder.Append($" {parameter.Letter}\"{((string)parameter).Replace("\"", "\"\"")}\"");
                    }
                    else
                    {
                        builder.Append($" {parameter.Letter}{(string)parameter}");
                    }
                }
                else
                {
                    if (parameter.Type == typeof(string) && !parameter.IsExpression)
                    {
                        builder.Append($" \"{((string)parameter).Replace("\"", "\"\"")}\"");
                    }
                    else
                    {
                        builder.Append($" {(string)parameter}");
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
            if (Result != null && !Result.IsEmpty)
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
            if (Keyword != KeywordType.None)
            {
                return KeywordToString();
            }

            if (Type == CodeType.Comment)
            {
                return "(comment)";
            }

            string prefix = Flags.HasFlag(CodeFlags.EnforceAbsolutePosition) ? "G53 " : string.Empty;
            if (MajorNumber != null)
            {
                if (MinorNumber != null)
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
#pragma warning disable CS0618 // Type or member is obsolete
                KeywordType.Return => "return",
#pragma warning restore CS0618 // Type or member is obsolete
                KeywordType.Abort => "abort",
                KeywordType.Var => "var",
                KeywordType.Set => "set",
                KeywordType.Echo => "echo",
                KeywordType.Global => "global",
                _ => throw new NotImplementedException()
            };
        }
    }
}
