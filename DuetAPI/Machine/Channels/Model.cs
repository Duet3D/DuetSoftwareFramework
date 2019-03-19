using System;
using System.Collections.Generic;
using System.Text;

namespace DuetAPI.Machine.Channels
{
    /// <summary>
    /// Information about the available G/M/T-code channels.
    /// <seealso cref="Commands.CodeChannel"/>
    /// </summary>
    public class Model : ICloneable
    {
        /// <summary>
        /// Main G/M/T-code channel (default)
        /// </summary>
        public Channel Main { get; set; } = new Channel();

        /// <summary>
        /// G/M/T-code channel for serial lines (may be a PanelDue over UART)
        /// </summary>
        public Channel Serial { get; set; } = new Channel();

        /// <summary>
        /// G/M/T-code channel for file prints
        /// </summary>
        public Channel File { get; set; } = new Channel();

        /// <summary>
        /// G/M/T-code channel for HTTP requests
        /// </summary>
        public Channel HTTP { get; set; } = new Channel();

        /// <summary>
        /// G/M/T-code channel for Telnet requests
        /// </summary>
        public Channel Telnet { get; set; } = new Channel();

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Model
            {
                Main = (Channel)Main.Clone(),
                Serial = (Channel)Serial.Clone(),
                File = (Channel)File.Clone(),
                HTTP = (Channel)HTTP.Clone(),
                Telnet = (Channel)Telnet.Clone()
            };
        }
    }
}
