using System.Collections.Generic;
using System.Text;

namespace DuetAPI.Commands
{
    /// <summary>
    /// List-based representation of a code result.
    /// Each item represents a <see cref="Message"/> instance which can be easily converted to a string
    /// </summary>
    public class CodeResult : List<Message>
    {
        public CodeResult() {}

        public override string ToString()
        {
            StringBuilder builder = new StringBuilder();
            foreach (Message message in this)
            {
                builder.AppendLine(message.ToString());
            }
            return builder.ToString();
        }
    }
}
