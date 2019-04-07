using System.Collections.Generic;
using System.Text;
using System.Linq;

namespace DuetAPI.Commands
{
    /// <summary>
    /// List-based representation of a code result.
    /// Each item represents a <see cref="Message"/> instance which can be easily converted to a string
    /// </summary>
    public class CodeResult : List<Message>
    {
        /// <summary>
        /// Create a new code result
        /// </summary>
        public CodeResult() { }

        /// <summary>
        /// Create a new code result with an initial message
        /// </summary>
        /// <param name="type">Message type</param>
        /// <param name="content">Message content</param>
        public CodeResult(MessageType type, string content)
        {
            Add(new Message(type, content));
        }

        /// <summary>
        /// Converts the CodeResult to a string
        /// </summary>
        /// <returns>The CodeResult as a string</returns>
        public override string ToString()
        {
            StringBuilder builder = new StringBuilder();
            foreach (Message message in this)
            {
                builder.AppendLine(message.ToString());
            }
            return builder.ToString();
        }

        /// <summary>
        /// Indicates if the code could complete without an error
        /// </summary>
        public bool IsSuccessful => !this.Any(item => item.Type == MessageType.Error);
    }
}
