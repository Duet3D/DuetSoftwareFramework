using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Possible states of the firmware
    /// </summary>
    [JsonConverter(typeof(JsonCamelCaseStringEnumConverter))]
    public enum MachineStatus
    {
        /// <summary>
        /// The firmware is being updated
        /// </summary>
        Updating,
        
        /// <summary>
        /// The machine is turned off (i.e. the input voltage is too low for operation)
        /// </summary>
        Off,
        
        /// <summary>
        /// The machine has encountered an emergency stop and is ready to reset
        /// </summary>
        Halted,
        
        /// <summary>
        /// The machine is about to pause a file job
        /// </summary>
        Pausing,
        
        /// <summary>
        /// The machine has paused a file job
        /// </summary>
        Paused,
        
        /// <summary>
        /// The machine is about to resume a paused file job
        /// </summary>
        Resuming,
        
        /// <summary>
        /// The machine is processing a file job
        /// </summary>
        Processing,
        
        /// <summary>
        /// The machine is simulating a file job to determine its printing time
        /// </summary>
        Simulating,
        
        /// <summary>
        /// The machine is busy doing something (e.g. moving)
        /// </summary>
        Busy,
        
        /// <summary>
        /// The machine is changing the current tool
        /// </summary>
        ChangingTool,
        
        /// <summary>
        /// The machine is on but has nothing to do
        /// </summary>
        Idle
    }
}