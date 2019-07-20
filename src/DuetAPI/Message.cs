using DuetAPI.Utility;
using System;

namespace DuetAPI
{
    /// <summary>
    /// Generic container for messages
    /// </summary>
    public sealed class Message : IAssignable, ICloneable
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
        /// Print this message to the console
        /// </summary>
        public void Print()
        {
            switch (Type)
            {
                case MessageType.Error:
                    Console.Write("[err] ");
                    break;
                case MessageType.Warning:
                    Console.Write("[warn] ");
                    break;
                case MessageType.Success:
                    Console.Write("[info] ");
                    break;
            }
            Console.WriteLine(Content);
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
        public string Content { get; set; } = "";

        /// <summary>
        /// Converts this message to a RepRapFirmware-style message
        /// </summary>
        /// <returns>RepRapFirmware-style message</returns>
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
        /// Assigns every property of another instance of this one
        /// </summary>
        /// <param name="from">Object to assign from</param>
        /// <exception cref="ArgumentNullException">other is null</exception>
        /// <exception cref="ArgumentException">Types do not match</exception>
        public void Assign(object from)
        {
            if (from == null)
            {
                throw new ArgumentNullException();
            }
            if (!(from is Message other))
            {
                throw new ArgumentException("Invalid type");
            }

            Time = other.Time;
            Type = other.Type;
            Content = string.Copy(other.Content);
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
                Content = string.Copy(Content)
            };
        }
    }
}
