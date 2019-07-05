using System;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the machine state
    /// </summary>
    public class State : ICloneable
    {
        /// <summary>
        /// State of the ATX power pin (if controlled)
        /// </summary>
        public bool? AtxPower { get; set; }
        
        /// <summary>
        /// Number of the currently selected tool or -1 if none is selected
        /// </summary>
        public int CurrentTool { get; set; } = -1;

        /// <summary>
        /// Current mode of operation
        /// </summary>
        public MachineMode Mode { get; set; } = MachineMode.FFF;
        
        /// <summary>
        /// Current state of the machine
        /// </summary>
        public MachineStatus Status { get; set; } = MachineStatus.Idle;

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new State
            {
                AtxPower = AtxPower,
                CurrentTool = CurrentTool,
                Mode = Mode,
                Status = Status
            };
        }
    }
}