using System;

namespace DuetAPI.Machine.Channels
{
    /// <summary>
    /// Information about the available G/M/T-code channels.
    /// <seealso cref="CodeChannel"/>
    /// </summary>
    public class Model : ICloneable
    {
        /// <summary>
        /// G/M/T-code channel for HTTP requests
        /// </summary>
        public Channel HTTP { get; set; } = new Channel();

        /// <summary>
        /// G/M/T-code channel for Telnet requests
        /// </summary>
        public Channel Telnet { get; set; } = new Channel();

        /// <summary>
        /// G/M/T-code channel for file prints
        /// </summary>
        public Channel File { get; set; } = new Channel();

        /// <summary>
        /// G/M/T-code channel for USB
        /// </summary>
        public Channel USB { get; set; } = new Channel();

        /// <summary>
        /// G/M/T-code channel for AUX (UART/PanelDue)
        /// </summary>
        public Channel AUX { get; set; } = new Channel();

        /// <summary>
        /// G/M/T-code channel for Daemon (deals with config.g and triggers)
        /// </summary>
        public Channel Daemon { get; set; } = new Channel();

        /// <summary>
        /// G/M/T-code channel for the code queue
        /// </summary>
        public Channel CodeQueue { get; set; } = new Channel();

        /// <summary>
        /// G/M/T-code channel for AUX (UART/PanelDue)
        /// </summary>
        public Channel LCD { get; set; } = new Channel();

        /// <summary>
        /// Default G/M/T-code channel for generic codes
        /// </summary>
        public Channel SPI { get; set; } = new Channel();

        /// <summary>
        /// Default G/M/T-code channel for generic codes
        /// </summary>
        public Channel AutoPause { get; set; } = new Channel();

        /// <summary>
        /// Index operator for simple access via the <see cref="CodeChannel"/> enum
        /// </summary>
        /// <param name="key">Channel to access</param>
        /// <returns></returns>
        public Channel this[CodeChannel key]
        {
            get
            {
                switch (key)
                {
                    case CodeChannel.HTTP: return HTTP;
                    case CodeChannel.Telnet: return Telnet;
                    case CodeChannel.File: return File;
                    case CodeChannel.USB: return USB;
                    case CodeChannel.AUX: return AUX;
                    case CodeChannel.Daemon: return Daemon;
                    case CodeChannel.CodeQueue: return CodeQueue;
                    case CodeChannel.LCD: return LCD;
                    case CodeChannel.AutoPause: return AutoPause;
                    default: return SPI;
                }
            }
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Model
            {
                HTTP = (Channel)HTTP.Clone(),
                Telnet = (Channel)Telnet.Clone(),
                File = (Channel)File.Clone(),
                USB = (Channel)USB.Clone(),
                AUX = (Channel)AUX.Clone(),
                Daemon = (Channel)Daemon.Clone(),
                CodeQueue = (Channel)CodeQueue.Clone(),
                LCD = (Channel)LCD.Clone(),
                SPI = (Channel)SPI.Clone(),
                AutoPause = (Channel)AutoPause.Clone()
            };
        }
    }
}
