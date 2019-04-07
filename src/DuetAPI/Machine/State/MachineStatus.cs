using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Possible states of the firmware
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum MachineStatus
    {
        /// <summary>
        /// The firmware is being updated
        /// </summary>
        [EnumMember(Value = "updating")]
        Updating,
        
        /// <summary>
        /// The machine is turned off (i.e. the input voltage is too low for operation)
        /// </summary>
        [EnumMember(Value = "off")]
        Off,
        
        /// <summary>
        /// The machine has encountered an emergency stop and is ready to reset
        /// </summary>
        [EnumMember(Value = "halted")]
        Halted,
        
        /// <summary>
        /// The machine is about to pause a file job
        /// </summary>
        [EnumMember(Value = "pausing")]
        Pausing,
        
        /// <summary>
        /// The machine has paused a file job
        /// </summary>
        [EnumMember(Value = "paused")]
        Paused,
        
        /// <summary>
        /// The machine is about to resume a paused file job
        /// </summary>
        [EnumMember(Value = "resuming")]
        Resuming,
        
        /// <summary>
        /// The machine is processing a file job
        /// </summary>
        [EnumMember(Value = "processing")]
        Processing,
        
        /// <summary>
        /// The machine is simulating a file job to determine its printing time
        /// </summary>
        [EnumMember(Value = "simulating")]
        Simulating,
        
        /// <summary>
        /// The machine is busy doing something (e.g. moving)
        /// </summary>
        [EnumMember(Value = "busy")]
        Busy,
        
        /// <summary>
        /// The machine is changing the current tool
        /// </summary>
        [EnumMember(Value = "changingTool")]
        ChangingTool,
        
        /// <summary>
        /// The machine is on but has nothing to do
        /// </summary>
        [EnumMember(Value = "idle")]
        Idle
    }
}