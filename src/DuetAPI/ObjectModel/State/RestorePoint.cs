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
        public ModelCollection<float> Coords { get; } = [];

        /// <summary>
        /// The virtual extruder position at the start of this move
        /// </summary>
        public float ExtruderPos
        {
            get => _extruderPos;
            set => SetPropertyValue(ref _extruderPos, value);
        }
        private float _extruderPos;

        /// <summary>
        /// PWM value of the tool fan (0..1)
        /// </summary>
        public float FanPwm
        {
            get => _fanPwm;
            set => SetPropertyValue(ref _fanPwm, value);
        }
        private float _fanPwm;


        /// <summary>
        /// Requested feedrate (in mm/s)
        /// </summary>
        public float FeedRate
        {
            get => _feedRate;
            set => SetPropertyValue(ref _feedRate, value);
        }
        private float _feedRate;

        /// <summary>
        /// The output port bits setting for this move or null if not applicable
        /// </summary>
        public int? IoBits
        {
            get => _ioBits;
            set => SetPropertyValue(ref _ioBits, value);
        }
        private int? _ioBits;

        /// <summary>
        /// Laser PWM value (0..1) or null if not applicable
        /// </summary>
        public float? LaserPwm
        {
            get => _laserPwm;
            set => SetPropertyValue(ref _laserPwm, value);
        }
        private float? _laserPwm;

        /// <summary>
        /// The tool number that was active
        /// </summary>
        public int ToolNumber
        {
            get => _toolNumber;
            set => SetPropertyValue(ref _toolNumber, value);
        }
        private int _toolNumber = -1;
    }
}
