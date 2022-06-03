using System.Text.Json.Serialization;
using System.Linq;
using System;

namespace DuetAPI.ObjectModel
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
        public InputChannel HTTP => this[CodeChannel.HTTP];

        /// <summary>
        /// G/M/T-code channel for Telnet requests
        /// </summary>
        [JsonIgnore]
        public InputChannel Telnet => this[CodeChannel.Telnet];

        /// <summary>
        /// G/M/T-code channel for primary file prints
        /// </summary>
        [JsonIgnore]
        public InputChannel File => this[CodeChannel.File];

        /// <summary>
        /// G/M/T-code channel for USB
        /// </summary>
        [JsonIgnore]
        public InputChannel USB => this[CodeChannel.USB];

        /// <summary>
        /// G/M/T-code channel for AUX (UART/PanelDue)
        /// </summary>
        [JsonIgnore]
        public InputChannel Aux => this[CodeChannel.Aux];

        /// <summary>
        /// G/M/T-code channel for running triggers or config.g
        /// </summary>
        [JsonIgnore]
        public InputChannel Trigger => this[CodeChannel.Trigger];

        /// <summary>
        /// G/M/T-code channel for the primary code queue
        /// </summary>
        [JsonIgnore]
        public InputChannel Queue => this[CodeChannel.Queue];

        /// <summary>
        /// G/M/T-code channel for AUX (UART/PanelDue)
        /// </summary>
        [JsonIgnore]
        public InputChannel LCD => this[CodeChannel.LCD];

        /// <summary>
        /// Default G/M/T-code channel for generic codes
        /// </summary>
        [JsonIgnore]
        public InputChannel SBC => this[CodeChannel.SBC];

        /// <summary>
        /// Code channel that executes the daemon process
        /// </summary>
        [JsonIgnore]
        public InputChannel Daemon => this[CodeChannel.Daemon];

        /// <summary>
        /// G/M/T-code chanel for auto pause events
        /// </summary>
        [JsonIgnore]
        public InputChannel Autopause => this[CodeChannel.Autopause];

        /// <summary>
        /// G/M/T-code channel for secondary file prints
        /// </summary>
        /// <remarks>
        /// May not be available if async moves are not supported
        /// </remarks>
        [JsonIgnore]
        public InputChannel File2 => this[CodeChannel.File2];

        /// <summary>
        /// G/M/T-code channel for the secondary code queue
        /// </summary>
        /// <remarks>
        /// May not be available if async moves are not supported
        /// </remarks>
        [JsonIgnore]
        public InputChannel Queue2 => this[CodeChannel.Queue2];

        /// <summary>
        /// Index operator for easy access via an <see cref="CodeChannel"/> value
        /// </summary>
        /// <param name="channel">Channel to retrieve information about</param>
        /// <returns>Information about the code channel</returns>
        public InputChannel this[CodeChannel channel] => this.FirstOrDefault(inputChannel => inputChannel != null && inputChannel.Name == channel);
    }
}
