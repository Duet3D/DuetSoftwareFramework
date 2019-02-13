using System;

namespace DuetAPI
{
    public enum MessageType
    {
        // Message types
        Success,
        Warning,
        Error,

        // Message destinatons
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
