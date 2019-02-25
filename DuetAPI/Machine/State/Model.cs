using System;

namespace DuetAPI.Machine.State
{
    /// <summary>
    /// Information about the machine state
    /// </summary>
    public class Model : ICloneable
    {
        /// <summary>
        /// Whether or not ATX power is on
        /// </summary>
        public bool AtxPower { get; set; }
        
        /// <summary>
        /// Number of the currently selected tool or -1 if none is selected
        /// </summary>
        public int CurrentTool { get; set; } = -1;

        /// <summary>
        /// Current mode of operation
        /// </summary>
        public Mode Mode { get; set; } = Mode.FFF;
        
        /// <summary>
        /// Whether relative extrusion is being used
        /// </summary>
        public bool RelativeExtrusion { get; set; }
        
        /// <summary>
        /// Whether relative positioning is being used
        /// </summary>
        public bool RelativePositioning { get; set; }
        
        /// <summary>
        /// Current state of the machine
        /// </summary>
        public Status Status { get; set; } = Status.Idle;

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Model
            {
                AtxPower = AtxPower,
                CurrentTool = CurrentTool,
                Mode = Mode,
                RelativeExtrusion = RelativeExtrusion,
                RelativePositioning = RelativePositioning,
                Status = Status
            };
        }
    }
}