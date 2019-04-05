using System;

namespace DuetAPI
{
    /// <summary>
    /// Type of a generic message
    /// </summary>
    public enum MessageType
    {
        /// <summary>
        /// This is a success message
        /// </summary>
        Success = 0,

        /// <summary>
        /// This is a warning message
        /// </summary>
        Warning,

        /// <summary>
        /// This is an error message
        /// </summary>
        Error
    }
    
    /// <summary>
    /// Generic container for messages
    /// </summary>
    public class Message : ICloneable
    {
        /// <summary>
        /// Create a nwe message
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
        public string Content { get; set; } = "";

        /// <summary>
        /// Converts this message to a RepRapFirmware-style message
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            switch (Type)
            {
                case MessageType.Error: return "Error: " + Content;
                case MessageType.Warning: return "Warning: " + Content;
                default: return Content;
            }
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>Clone of this instance</returns>
        public object Clone()
        {
            return new Message
            {
                Time = Time,
                Type = Type,
                Content = Content
            };
        }
    }
}
