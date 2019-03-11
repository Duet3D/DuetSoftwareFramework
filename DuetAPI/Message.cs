using System;

// Not documented in detail yet because this is not 100% final. At the moment it's just more or less copied & pasted from RRF
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

namespace DuetAPI
{
    [Flags]
    public enum MessageType
    {
        // Message types
        Success,
        Warning,
        Error,

        // Message destinations
        Usb,
        Lcd,
        Http,
        Telnet,
        Aux,
        Log
    }

    public class Message : ICloneable
    {
        public const MessageType Debug = MessageType.Usb;			                                                    // A debug message to send in blocking mode to USB
        public const MessageType Generic = MessageType.Usb | MessageType.Lcd | MessageType.Http | MessageType.Telnet;   // A message that is to be sent to the web, Telnet, USB and panel
        public const MessageType Error = Generic | MessageType.Log | MessageType.Error;                                 // An error message
        public const MessageType Warning = Generic | MessageType.Log | MessageType.Warning;                             // A warning message

        public DateTime Time { get; set; } = DateTime.Now;
        public MessageType Type { get; set; } = Generic;
        public string Content { get; set; } = "";

        public override string ToString()
        {
            string prefix = "";
            if (Type.HasFlag(Error))
            {
                prefix = "Error: ";
            }
            else if (Type.HasFlag(Warning))
            {
                prefix = "Warning: ";
            }
            return prefix + Content;
        }

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
