using System;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Generic container for messages
    /// </summary>
    public sealed class Message
    {
        /// <summary>
        /// Create a new message
        /// </summary>
        public Message() { }

        /// <summary>
        /// Create a new message
        /// </summary>
        /// <param name="type">Message type</param>
        /// <param name="content">Message content</param>
        public Message(MessageType type, string content = "")
        {
            Type = type;
            Content = content;
        }

        /// <summary>
        /// Content of this message
        /// </summary>
        /// <remarks>May be empty but not null</remarks>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// Time at which the message was generated
        /// </summary>
        public DateTime Time { get; set; } = DateTime.Now;

        /// <summary>
        /// Type of this message
        /// </summary>
        public MessageType Type { get; set; } = MessageType.Success;

        /// <summary>
        /// Replace the content if empty or append a new line that is not empty
        /// </summary>
        /// <param name="line">Line content</param>
        public void AppendLine(string line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                if (string.IsNullOrEmpty(Content))
                {
                    Content = line;
                }
                else
                {
                    if (!Content.EndsWith('\n'))
                    {
                        Content += '\n';
                    }
                    Content += line;
                }
            }
        }

        /// <summary>
        /// Append another message to this one, potentially overwriting the message type
        /// </summary>
        /// <param name="other">Message to append</param>
        public void Append(Message other)
        {
            if (other != null)
            {
                if (other.Type > Type)
                {
                    Type = other.Type;
                }
                if (!string.IsNullOrEmpty(other.Content))
                {
                    AppendLine(other.Content);
                }
            }
        }

        /// <summary>
        /// Append another message to this one, potentially overwriting the message type
        /// </summary>
        /// <param name="type">Message type</param>
        /// <param name="content">Message content</param>
        public void Append(MessageType type, string content)
        {
            if (type > Type)
            {
                Type = type;
            }
            if (!string.IsNullOrEmpty(content))
            {
                AppendLine(content);
            }
        }

        /// <summary>
        /// Converts this message to a RepRapFirmware-style message
        /// </summary>
        /// <returns>RepRapFirmware-style message</returns>
        public override string ToString()
        {
            return Type switch
            {
                MessageType.Error => "Error: " + Content,
                MessageType.Warning => "Warning: " + Content,
                _ => Content,
            };
        }
    }
}
