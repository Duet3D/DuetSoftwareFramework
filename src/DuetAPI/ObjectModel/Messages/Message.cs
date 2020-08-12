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
        /// Time at which the message was generated
        /// </summary>
        public DateTime Time { get; set; } = DateTime.Now;

        /// <summary>
        /// Type of this message
        /// </summary>
        public MessageType Type { get; set; } = MessageType.Success;

        /// <summary>
        /// Content of this message
        /// </summary>
        /// <remarks>May be empty but not null</remarks>
        public string Content { get; set; } = string.Empty;

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
