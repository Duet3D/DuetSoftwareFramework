namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Class holding information about a restore point
    /// </summary>
    public sealed class RestorePoint : ModelObject
    {
        /// <summary>
        /// Axis coordinates of the restore point (in mm)
        /// </summary>
        public ModelCollection<float> Coords { get; } = new ModelCollection<float>();

        /// <summary>
        /// The virtual extruder position at the start of this move
        /// </summary>
        public float ExtruderPos { get; set; }

        /// <summary>
        /// Requested feedrate (in mm/s)
        /// </summary>
        public float FeedRate { get; set; }

        /// <summary>
        /// The output port bits setting for this move or null if not applicable
        /// </summary>
        public int? IoBits { get; set; }

        /// <summary>
        /// Laser PWM value (0..1) or null if not applicable
        /// </summary>
        public float? LaserPwm { get; set; }

        /// <summary>
        /// The spindle RPMs that were set, negative if anticlockwise direction
        /// </summary>
        public ModelCollection<float> SpindleSpeeds { get; } = new ModelCollection<float>();

        /// <summary>
        /// The tool number that was active
        /// </summary>
        public int ToolNumber { get; set; }
    }
}
