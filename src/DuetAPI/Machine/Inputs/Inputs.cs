using System.Text.Json.Serialization;
using System.Linq;
using System;

namespace DuetAPI.Machine
{
    /// <summary>
    /// List holding information about the available G/M/T-code channels
    /// </summary>
    /// <seealso cref="InputChannel"/>
    /// <remarks>
    /// During runtime some elements may be replaced with null if they are not available
    /// </remarks>
    public sealed class Inputs : ModelCollection<InputChannel>
    {
        /// <summary>
        /// Total number of supported input channels
        /// </summary>
        public static readonly int Total = Enum.GetValues(typeof(CodeChannel)).Length;

        /// <summary>
        /// Constructor of this class
        /// </summary>
        public Inputs() : base()
        {
            foreach (CodeChannel name in Enum.GetValues(typeof(CodeChannel)))
            {
                Add(new InputChannel() { Name = name });
            }
        }

        /// <summary>
        /// G/M/T-code channel for HTTP requests
        /// </summary>
        [JsonIgnore]
        public InputChannel HTTP
        {
            get => this[CodeChannel.HTTP];
        }

        /// <summary>
        /// G/M/T-code channel for Telnet requests
        /// </summary>
        [JsonIgnore]
        public InputChannel Telnet
        {
            get => this[CodeChannel.Telnet];
        }

        /// <summary>
        /// G/M/T-code channel for file prints
        /// </summary>
        [JsonIgnore]
        public InputChannel File
        {
            get => this[CodeChannel.File];
        }

        /// <summary>
        /// G/M/T-code channel for USB
        /// </summary>
        [JsonIgnore]
        public InputChannel USB
        {
            get => this[CodeChannel.USB];
        }

        /// <summary>
        /// G/M/T-code channel for AUX (UART/PanelDue)
        /// </summary>
        [JsonIgnore]
        public InputChannel Aux
        {
            get => this[CodeChannel.Aux];
        }

        /// <summary>
        /// G/M/T-code channel for running triggers or config.g
        /// </summary>
        [JsonIgnore]
        public InputChannel Trigger
        {
            get => this[CodeChannel.Trigger];
        }

        /// <summary>
        /// G/M/T-code channel for the code queue
        /// </summary>
        [JsonIgnore]
        public InputChannel Queue
        {
            get => this[CodeChannel.Queue];
        }

        /// <summary>
        /// G/M/T-code channel for AUX (UART/PanelDue)
        /// </summary>
        [JsonIgnore]
        public InputChannel LCD
        {
            get => this[CodeChannel.LCD];
        }

        /// <summary>
        /// Default G/M/T-code channel for generic codes
        /// </summary>
        [JsonIgnore]
        public InputChannel SBC
        {
            get => this[CodeChannel.SBC];
        }

        /// <summary>
        /// Code channel that executes the daemon process
        /// </summary>
        [JsonIgnore]
        public InputChannel Daemon
        {
            get => this[CodeChannel.Daemon];
        }

        /// <summary>
        /// G/M/T-code chanel for auto pause events
        /// </summary>
        [JsonIgnore]
        public InputChannel Autopause
        {
            get => this[CodeChannel.Autopause];
        }

        /// <summary>
        /// Index operator for easy access via an <see cref="CodeChannel"/> value
        /// </summary>
        /// <param name="channel">Channel to retrieve information about</param>
        /// <returns>Information about the code channel</returns>
        public InputChannel this[CodeChannel channel]
        {
            get => this.FirstOrDefault(inputChannel => inputChannel.Name == channel);
        }
    }
}
