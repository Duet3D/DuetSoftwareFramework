using DuetAPI.Utility;
using System;
using System.Text.Json.Serialization;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the available G/M/T-code channels.
    /// <seealso cref="CodeChannel"/>
    /// </summary>
    public sealed class Channels : IAssignable, ICloneable
    {
        /// <summary>
        /// Total number of code channels
        /// </summary>
        public static readonly int Total = Enum.GetValues(typeof(CodeChannel)).Length;

        /// <summary>
        /// G/M/T-code channel for HTTP requests
        /// </summary>
        public Channel HTTP { get; private set; } = new Channel();

        /// <summary>
        /// G/M/T-code channel for Telnet requests
        /// </summary>
        public Channel Telnet { get; private set; } = new Channel() { Compatibility = Compatibility.Marlin };

        /// <summary>
        /// G/M/T-code channel for file prints
        /// </summary>
        public Channel File { get; private set; } = new Channel();

        /// <summary>
        /// G/M/T-code channel for USB
        /// </summary>
        public Channel USB { get; private set; } = new Channel() { Compatibility = Compatibility.Marlin };

        /// <summary>
        /// G/M/T-code channel for AUX (UART/PanelDue)
        /// </summary>
        public Channel AUX { get; private set; } = new Channel();

        /// <summary>
        /// G/M/T-code channel for Daemon (deals with config.g and triggers)
        /// </summary>
        public Channel Daemon { get; private set; } = new Channel();

        /// <summary>
        /// G/M/T-code channel for the code queue
        /// </summary>
        public Channel CodeQueue { get; private set; } = new Channel();

        /// <summary>
        /// G/M/T-code channel for AUX (UART/PanelDue)
        /// </summary>
        public Channel LCD { get; private set; } = new Channel();

        /// <summary>
        /// Default G/M/T-code channel for generic codes
        /// </summary>
        public Channel SPI { get; private set; } = new Channel();

        /// <summary>
        /// GM/T-code chanel for auto pause events
        /// </summary>
        public Channel AutoPause { get; private set; } = new Channel();

        /// <summary>
        /// Index operator for simple access via the <see cref="CodeChannel"/> enum
        /// </summary>
        /// <param name="key">Channel to access</param>
        /// <returns>Channel instance</returns>
        [JsonIgnore]
        public Channel this[CodeChannel key]
        {
            get
            {
                return key switch
                {
                    CodeChannel.HTTP => HTTP,
                    CodeChannel.Telnet => Telnet,
                    CodeChannel.File => File,
                    CodeChannel.USB => USB,
                    CodeChannel.AUX => AUX,
                    CodeChannel.Daemon => Daemon,
                    CodeChannel.CodeQueue => CodeQueue,
                    CodeChannel.LCD => LCD,
                    CodeChannel.AutoPause => AutoPause,
                    _ => SPI,
                };
            }
        }

        /// <summary>
        /// Assigns every property from another instance
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
            if (!(from is Channels other))
            {
                throw new ArgumentException("Invalid type");
            }

            HTTP.Assign(other.HTTP);
            Telnet.Assign(other.Telnet);
            File.Assign(other.File);
            USB.Assign(other.USB);
            AUX.Assign(other.AUX);
            Daemon.Assign(other.Daemon);
            CodeQueue.Assign(other.CodeQueue);
            LCD.Assign(other.LCD);
            SPI.Assign(other.SPI);
            AutoPause.Assign(other.AutoPause);
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>Clone of this instance</returns>
        public object Clone()
        {
            return new Channels
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
